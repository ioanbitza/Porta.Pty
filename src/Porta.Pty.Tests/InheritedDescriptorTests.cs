// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The program of one terminal must not inherit the descriptors of another.
    ///
    /// <para>forkpty does not mark the controller close-on-exec, and the poller of the non-blocking path
    /// makes its wakeup pipe without O_CLOEXEC, so every program started after terminal A held A's
    /// controller (and the pipe) as an open descriptor: the program of terminal B could type into
    /// terminal A. Measured on macOS 27 (arm64) and Ubuntu (arm64) on 16.10.2026: B saw
    /// "fd 39 -> /dev/ptmx" and A echoed what B wrote; on the non-blocking path B also saw the two ends
    /// of the poller's pipe.</para>
    ///
    /// <para>Terminal B lists its own descriptors above 2 with shell builtins only (test -e on
    /// /dev/fd/N for N from 3 to 1023), so no command it runs opens a descriptor of its own (ls opens
    /// 3 and 4 on macOS), then writes a marker into every inherited descriptor that is a terminal. A sentinel written by the test into A afterwards proves that A's output was read past
    /// the point where B's write would have arrived, so the absence of the marker is not a timing
    /// accident.</para>
    /// </summary>
    [TestClass]
    public class InheritedDescriptorTests
    {
        private const string Injected = "INJECTED-BY-B";

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 20_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static PtyOptions Shell(string name, string app, string[] commandLine, bool useAsyncIo) => new PtyOptions
        {
            Name = name,
            Cols = 200,
            Rows = 25,
            Cwd = Environment.CurrentDirectory,
            App = app,
            CommandLine = commandLine,
            VerbatimCommandLine = true,
            Environment = new Dictionary<string, string>(),
            UseAsyncIo = useAsyncIo,
        };

        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_ProgramOfTerminalB_SeesNoDescriptorOfTerminalA_AndCannotTypeIntoIt(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("fork/exec descriptor inheritance is a Unix concern; ConPTY starts its own console host");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);

            using IPtyConnection terminalA = await PtyProvider.SpawnAsync(
                Shell("A", "/bin/cat", Array.Empty<string>(), useAsyncIo), cts.Token);
            var outputA = new Pump(terminalA);

            const string script =
                "fds=''; n=3; "
                + "while [ $n -lt 1024 ]; do if [ -e /dev/fd/$n ]; then fds=\"$fds $n\"; fi; n=$((n+1)); done; "
                + "echo FDS-BEGIN $fds FDS-END; "
                + "for n in $fds; do "
                + "if [ -t \"$n\" ]; then printf '" + Injected + "\\n' >&\"$n\"; fi; "
                + "done; "
                + "echo B-DONE";

            using IPtyConnection terminalB = await PtyProvider.SpawnAsync(
                Shell("B", "/bin/sh", new[] { "-c", script }, useAsyncIo), cts.Token);
            var outputB = new Pump(terminalB);

            (await outputB.WaitForAsync("B-DONE", TestTimeoutMs)).Should().BeTrue(
                "terminal B's script should finish; its output was: {0}", outputB.Text);
            terminalB.WaitForExit(TestTimeoutMs).Should().BeTrue("terminal B's script should exit");

            var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
            var bytes = Encoding.ASCII.GetBytes(sentinel + "\n");
            await terminalA.WriterStream.WriteAsync(bytes, 0, bytes.Length, cts.Token);
            await terminalA.WriterStream.FlushAsync(cts.Token);
            (await outputA.WaitForAsync(sentinel, TestTimeoutMs)).Should().BeTrue(
                "terminal A should echo the sentinel the test wrote into it; its output was: {0}", outputA.Text);

            var listed = InheritedDescriptors(outputB.Text);

            using var scope = new AwesomeAssertions.Execution.AssertionScope();

            listed.Should().BeEmpty(
                "the program of terminal B should hold only stdin, stdout and stderr, "
                + "but it listed the descriptors above 2 as [{0}] (useAsyncIo={1})",
                string.Join(", ", listed),
                useAsyncIo);

            outputA.Text.Should().NotContain(
                Injected,
                "the program of terminal B must not be able to type into terminal A (useAsyncIo={0})",
                useAsyncIo);
        }

        /// <summary>
        /// Guards the macOS close loop against skipping fd 3 (<c>&gt; 3</c> for <c>&gt; 2</c>), with more
        /// descriptors open than one listing pass returns. The host holds 600 descriptors made without
        /// O_CLOEXEC, and fd 3 is made inheritable for the length of the test (opened on /dev/null when free,
        /// its close-on-exec cleared and restored otherwise), so a child that inherited any of them lists it.
        /// The listing runs to 4095, past the 600. A loop limited to a single pass of 512 still passes here,
        /// because a loop that ends without a pass closing nothing falls through to the walk of the whole
        /// table, which closes the rest (measured: one pass stopped at fd 516 and B listed none).
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_ProgramSeesNoneOfSixHundredInheritableDescriptors_Fd3Included(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("fork/exec descriptor inheritance is a Unix concern; ConPTY starts its own console host");
            }

            var opened = new List<int>();
            var restoreFd3 = MakeFd3Inheritable();
            try
            {
                for (var i = 0; i < 600; i++)
                {
                    var fd = open("/dev/null", O_RDONLY);
                    if (fd < 0)
                    {
                        Assert.Inconclusive($"open(/dev/null) number {i} failed with errno {Marshal.GetLastPInvokeError()}: the host cannot hold 600 descriptors");
                    }

                    opened.Add(fd);
                }

                using var cts = new CancellationTokenSource(TestTimeoutMs);
                const string script =
                    "fds=''; n=3; "
                    + "while [ $n -lt 4096 ]; do if [ -e /dev/fd/$n ]; then fds=\"$fds $n\"; fi; n=$((n+1)); done; "
                    + "echo FDS-BEGIN $fds FDS-END; echo B-DONE";
                using IPtyConnection terminalB = await PtyProvider.SpawnAsync(
                    Shell("B", "/bin/sh", new[] { "-c", script }, useAsyncIo), cts.Token);
                var outputB = new Pump(terminalB);
                (await outputB.WaitForAsync("B-DONE", TestTimeoutMs)).Should().BeTrue(
                    "terminal B's script should finish; its output was: {0}", outputB.Text);

                var listed = InheritedDescriptors(outputB.Text);
                listed.Should().BeEmpty(
                    "the program should hold only stdin, stdout and stderr while the host holds {0} inheritable descriptors "
                    + "(fd 3 and {1} to {2}), but it listed [{3}] (useAsyncIo={4})",
                    opened.Count + 1,
                    opened[0],
                    opened[^1],
                    string.Join(", ", listed),
                    useAsyncIo);
            }
            finally
            {
                foreach (var fd in opened)
                {
                    close(fd);
                }

                restoreFd3();
            }
        }

        /// <summary>Makes fd 3 inheritable and returns how to put it back as it was.</summary>
        private static Action MakeFd3Inheritable()
        {
            const int Fd3 = 3;
            var flags = fcntl(Fd3, F_GETFD);
            if (flags == -1)
            {
                var devNull = open("/dev/null", O_RDONLY);
                devNull.Should().BeGreaterThan(2, $"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}");
                dup2(devNull, Fd3).Should().Be(Fd3, $"dup2 onto fd 3 failed with errno {Marshal.GetLastPInvokeError()}");
                close(devNull);
                return () => close(Fd3);
            }

            if ((flags & FD_CLOEXEC) == 0)
            {
                return () => { };
            }

            ioctl(Fd3, FIONCLEX).Should().Be(0, $"clearing close-on-exec on fd 3 failed with errno {Marshal.GetLastPInvokeError()}");
            return () => ioctl(Fd3, FIOCLEX);
        }

        private const int O_RDONLY = 0;
        private const int F_GETFD = 1;
        private const int FD_CLOEXEC = 1;

        private static ulong FIOCLEX => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x20006601UL : 0x5451UL;

        private static ulong FIONCLEX => RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x20006602UL : 0x5450UL;

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int dup2(int fd, int target);

        /// <summary>fcntl without its variadic third argument, which F_GETFD does not take.</summary>
        [DllImport("libc", SetLastError = true)]
        private static extern int fcntl(int fd, int command);

        /// <summary>ioctl without its variadic third argument, which FIOCLEX and FIONCLEX do not take.</summary>
        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, ulong request);

        private static int[] InheritedDescriptors(string output)
        {
            var begin = output.IndexOf("FDS-BEGIN", StringComparison.Ordinal);
            var end = output.IndexOf("FDS-END", StringComparison.Ordinal);
            if (begin < 0 || end < begin)
            {
                throw new AssertFailedException($"terminal B printed no descriptor listing; its output was: {output}");
            }

            return output.Substring(begin + "FDS-BEGIN".Length, end - begin - "FDS-BEGIN".Length)
                .Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .Where(fd => fd > 2)
                .ToArray();
        }

        /// <summary>Reads a terminal's output on a pool thread until it ends, so a wait can time out.</summary>
        private sealed class Pump
        {
            private readonly StringBuilder text = new StringBuilder();

            public Pump(IPtyConnection terminal)
            {
                _ = Task.Run(() =>
                {
                    var buffer = new byte[4096];
                    try
                    {
                        int read;
                        while ((read = terminal.ReaderStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            lock (this.text)
                            {
                                this.text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // The stream ends with an error on some platforms once the child is gone.
                    }
                });
            }

            public string Text
            {
                get
                {
                    lock (this.text)
                    {
                        return this.text.ToString();
                    }
                }
            }

            public async Task<bool> WaitForAsync(string marker, int timeoutMs)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < timeoutMs)
                {
                    if (this.Text.Contains(marker, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    await Task.Delay(20);
                }

                return this.Text.Contains(marker, StringComparison.Ordinal);
            }
        }
    }
}
