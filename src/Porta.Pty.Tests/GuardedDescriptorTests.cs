// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// macOS: a host that holds a descriptor with a close guard can still start a program.
    ///
    /// <para>close(2) of a descriptor guarded with GUARD_CLOSE (guarded_open_np, change_fdguard_np) raises
    /// EXC_GUARD, which kills the process, and Apple's libsqlite3 opens its database files with such a guard
    /// (measured on macOS 27: guard flags 0xf). The forked child therefore marks its descriptors close-on-exec
    /// on macOS instead of closing them. The kernel also makes a close-guarded descriptor close-on-fork, so
    /// on macOS 27 the child does not get this one at all and the test passes with the close loop as well;
    /// it pins that such a host gets a program that runs and exits on its own.</para>
    /// </summary>
    [TestClass]
    public class GuardedDescriptorTests
    {
        private const string Marker = "GUARDED-HOST-RAN";
        private const int O_RDONLY = 0;
        private const uint GUARD_CLOSE = 1u << 0;
        private const uint GUARD_DUP = 1u << 1;
        private const int PROC_PIDFDVNODEINFO = 1;
        private const int SizeOfVnodeFdInfo = 176;
        private const int GuardFlagsOffset = 20;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Mac_AHostHoldingACloseGuardedDescriptor_StartsAProgramThatRunsAndExits(bool useAsyncIo)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Assert.Inconclusive("descriptor guards exist only on macOS");
            }

            var fd = open("/dev/null", O_RDONLY);
            fd.Should().BeGreaterThan(2, $"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}");
            var guard = 0x506F7274612E5074UL;
            var fdFlags = 0;
            if (AddGuard(fd, IntPtr.Zero, 0, ref guard, GUARD_CLOSE | GUARD_DUP, ref fdFlags) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                close(fd);
                Assert.Inconclusive($"change_fdguard_np could not guard a descriptor of the test host (errno {errno})");
            }

            try
            {
                GuardFlags(fd).Should().Be(GUARD_CLOSE | GUARD_DUP, "the test host should hold fd {0} with a close guard", fd);

                using var cts = new CancellationTokenSource(TestTimeoutMs);
                using var terminal = await PtyProvider.SpawnAsync(
                    new PtyOptions
                    {
                        Name = "guarded host",
                        Cols = 80,
                        Rows = 25,
                        Cwd = Environment.CurrentDirectory,
                        App = "/bin/sh",
                        CommandLine = new[] { "-c", $"echo {Marker}; exit 3" },
                        VerbatimCommandLine = true,
                        Environment = new Dictionary<string, string>(),
                        UseAsyncIo = useAsyncIo,
                    },
                    cts.Token);

                var output = ReadUntil(terminal, Marker);
                output.Should().Contain(
                    Marker,
                    "the program should run while the host holds fd {0} with a close guard (useAsyncIo={1})",
                    fd,
                    useAsyncIo);
                terminal.WaitForExit(TestTimeoutMs).Should().BeTrue("the program should exit (useAsyncIo={0})", useAsyncIo);
                terminal.ExitCode.Should().Be(3, "the program should end with its own exit code, not be killed (useAsyncIo={0})", useAsyncIo);
            }
            finally
            {
                // The guard comes off first: close(2) of the guarded descriptor would kill the test host itself.
                RemoveGuard(fd, ref guard, GUARD_CLOSE | GUARD_DUP, IntPtr.Zero, 0, ref fdFlags).Should().Be(
                    0,
                    $"change_fdguard_np could not remove the guard of fd {fd} (errno {Marshal.GetLastPInvokeError()})");
                close(fd);
            }
        }

        /// <summary>The guard flags the kernel reports for one of this process's descriptors (fi_guardflags).</summary>
        private static uint GuardFlags(int fd)
        {
            var info = new byte[SizeOfVnodeFdInfo];
            proc_pidfdinfo(Environment.ProcessId, fd, PROC_PIDFDVNODEINFO, info, info.Length).Should().Be(
                info.Length,
                $"proc_pidfdinfo of fd {fd} failed with errno {Marshal.GetLastPInvokeError()}");
            return BitConverter.ToUInt32(info, GuardFlagsOffset);
        }

        /// <summary>Reads the terminal's output on a pool thread until the marker shows, the output ends or the time is up.</summary>
        private static string ReadUntil(IPtyConnection terminal, string marker)
        {
            var text = new StringBuilder();
            var reader = Task.Run(() =>
            {
                var buffer = new byte[4096];
                try
                {
                    int read;
                    while ((read = terminal.ReaderStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (text)
                        {
                            text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            if (text.ToString().Contains(marker, StringComparison.Ordinal))
                            {
                                return;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // The stream ends with an error on some platforms once the child is gone.
                }
            });
            reader.Wait(TestTimeoutMs);
            lock (text)
            {
                return text.ToString();
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int size);

        /// <summary>change_fdguard_np on an unguarded descriptor: no current guard, and the new one.</summary>
        [DllImport("libc", EntryPoint = "change_fdguard_np", SetLastError = true)]
        private static extern int AddGuard(int fd, IntPtr noGuard, uint noFlags, ref ulong guard, uint guardFlags, ref int fdFlags);

        /// <summary>change_fdguard_np on a guarded descriptor: the current guard, and no new one.</summary>
        [DllImport("libc", EntryPoint = "change_fdguard_np", SetLastError = true)]
        private static extern int RemoveGuard(int fd, ref ulong guard, uint guardFlags, IntPtr noGuard, uint noFlags, ref int fdFlags);
    }
}
