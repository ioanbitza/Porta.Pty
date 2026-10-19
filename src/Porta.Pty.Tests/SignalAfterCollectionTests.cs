// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Once the child has been collected, Kill and Dispose must not signal its pid (issue #36).
    ///
    /// <para>A collected pid is free, and the kernel hands it to the next process that starts. Kill and
    /// Dispose signalled the pid unconditionally (SIGHUP, and on macOS SIGKILL 100 ms later), so a
    /// connection disposed after its program had exited could kill an unrelated process: measured on
    /// Ubuntu with ns_last_pid, where an innocent sleep given the same pid died with code 129 (SIGHUP),
    /// on the blocking and on the non-blocking path.</para>
    ///
    /// <para>macOS gives no way to hand a chosen pid to a new process, so the signal is counted instead:
    /// the connections here are the library's own Mac and Linux connections, with only the platform
    /// kill step overridden to count its calls. The child is a real one, started by the test with
    /// posix_spawn, ended by the test with SIGTERM once the library watches it, and collected by the
    /// library's own watcher (blocking path) or reaper (non-blocking path); the controller is /dev/null, since no terminal is needed to collect a child. The count
    /// must stay at zero after the collection. A control test shows the count does move for a live
    /// child, so a zero is not the counter failing to see the call.</para>
    /// </summary>
    [TestClass]
    public class SignalAfterCollectionTests
    {
        private const int Iterations = 10;
        private const int O_RDWR = 2;
        private const int SIGTERM = 15;
        private const int WNOHANG = 1;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [TestMethod]
        [DataRow("Kill", false, DisplayName = "Kill, blocking")]
        [DataRow("Kill", true, DisplayName = "Kill, non-blocking")]
        [DataRow("Dispose", false, DisplayName = "Dispose, blocking")]
        [DataRow("Dispose", true, DisplayName = "Dispose, non-blocking")]
        public void Unix_AfterTheChildIsCollected_KillAndDisposeSendNoSignal(string call, bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            var signalled = new List<string>();
            var lost = new List<string>();
            for (var i = 0; i < Iterations; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                using var connection = CountingConnection.Create(OpenDevNull(), pid, useAsyncIo, forward: false);

                // The test ends the child itself, after the library has started watching it, so the exit
                // cannot race the watcher's registration; the library then collects it on its own.
                kill(pid, SIGTERM).Should().Be(0, $"iteration {i}: kill(2) of the running child failed with errno {Marshal.GetLastPInvokeError()}");

                if (!connection.Connection.WaitForExit(TestTimeoutMs))
                {
                    // Not this test's defect: the non-blocking reaper on macOS was measured (19.10.2026, full
                    // suite, 3 runs of 4) leaving an exited child a zombie for good. A child nobody collected
                    // still owns its pid, so signalling it is safe and says nothing about the behaviour under
                    // test; the iteration is recorded and the zombie collected here.
                    var probe = kill(pid, 0);
                    lost.Add($"iteration {i}: child {pid} not reported within {TestTimeoutMs} ms (kill(pid, 0) = {probe}, errno {Marshal.GetLastPInvokeError()})");
                    var status = 0;
                    waitpid(pid, ref status, 0);
                    continue;
                }

                if (call == "Kill")
                {
                    connection.Connection.Kill();
                }

                connection.Connection.Dispose();

                if (connection.PlatformKills > 0)
                {
                    signalled.Add($"iteration {i}: pid {pid} signalled {connection.PlatformKills} time(s) after collection");
                }
            }

            if (lost.Count == Iterations)
            {
                Assert.Inconclusive($"the library collected none of the {Iterations} children: {string.Join("; ", lost)}");
            }

            signalled.Should().BeEmpty(
                "{0} after the child was collected must not signal its pid, which may already belong to another process "
                + "(useAsyncIo={1}); {2} of {3} collected iterations did (children the library never collected: [{4}])",
                call,
                useAsyncIo,
                signalled.Count,
                Iterations - lost.Count,
                string.Join("; ", lost));
        }

        /// <summary>Control: a live child still reaches the platform kill step and dies of it.</summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Unix_KillOfALiveChild_StillSignalsIt(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            var pid = SpawnChild("exec /bin/sleep 600");
            using var connection = CountingConnection.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

            connection.Connection.Kill();

            connection.PlatformKills.Should().Be(1, "Kill of a running child should go through the platform kill step once");

            if (!connection.Connection.WaitForExit(TestTimeoutMs))
            {
                // A dead child the library did not report is the exit the non-blocking reaper sometimes
                // loses, not this test's subject; a child still running means Kill did not kill it.
                var status = 0;
                var collected = waitpid(pid, ref status, WNOHANG);
                if (collected != pid)
                {
                    kill(pid, 9);
                    waitpid(pid, ref status, 0);
                }

                collected.Should().Be(pid, "the child should die of the signal Kill sends, but it was still running {0} ms after Kill", TestTimeoutMs);
                Assert.Inconclusive(
                    $"child {pid} died of the signal Kill sent, but the library did not report its exit within {TestTimeoutMs} ms "
                    + "(the lost exit of the non-blocking reaper); collected by the test");
            }
        }

        /// <summary>
        /// macOS: Dispose after the child has exited must not wait out the 100 ms grace period meant for a
        /// child that is still running. The fastest of five (of those the library saw exit) is compared with
        /// half the grace period: the unconditional Thread.Sleep(100) makes each one take 100 ms or more (100.2 to 102.1 ms
        /// measured on 19.10.2026), and a Dispose with nothing to wait for takes a few milliseconds, so
        /// neither a loaded machine nor clock jitter around 100 ms decides the result.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Mac_DisposeAfterTheChildExited_DoesNotWaitTheKillGracePeriod(bool useAsyncIo)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Assert.Inconclusive("the 100 ms grace period between SIGHUP and SIGKILL exists only on macOS");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var durations = new List<double>();
            var lost = 0;
            for (var i = 0; i < 5; i++)
            {
                var terminal = await PtyProvider.SpawnAsync(
                    new PtyOptions
                    {
                        Name = "dispose-after-exit",
                        Cols = 80,
                        Rows = 25,
                        Cwd = Environment.CurrentDirectory,
                        App = "/bin/sh",
                        CommandLine = new[] { "-c", "exit 0" },
                        VerbatimCommandLine = true,
                        Environment = new Dictionary<string, string>(),
                        UseAsyncIo = useAsyncIo,
                    },
                    cts.Token);

                if (!terminal.WaitForExit(TestTimeoutMs))
                {
                    // The exit the non-blocking reaper sometimes loses (see the first test): a child the library
                    // still believes alive is entitled to its grace period, so it is not timed.
                    lost++;
                    terminal.Dispose();
                    continue;
                }

                var clock = Stopwatch.StartNew();
                terminal.Dispose();
                durations.Add(clock.Elapsed.TotalMilliseconds);
            }

            if (durations.Count == 0)
            {
                Assert.Inconclusive($"the library reported the exit of none of the {lost} children");
            }

            durations.Min().Should().BeLessThan(
                50,
                "Dispose of a terminal whose program has already exited has nothing to wait for (useAsyncIo={0}); took [{1}] ms",
                useAsyncIo,
                string.Join(", ", durations.Select(d => d.ToString("F1"))));
        }

        private static int SpawnChild(string command)
        {
            var argv = new string?[] { "/bin/sh", "-c", command, null };
            var envp = new string?[] { null };
            var rc = posix_spawn(out var pid, "/bin/sh", IntPtr.Zero, IntPtr.Zero, argv, envp);
            rc.Should().Be(0, "posix_spawn of /bin/sh should succeed");
            pid.Should().BePositive();
            return pid;
        }

        private static int OpenDevNull()
        {
            var fd = open("/dev/null", O_RDWR);
            fd.Should().BeGreaterThan(2, $"open(/dev/null) failed with errno {Marshal.GetLastPInvokeError()}");
            return fd;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int posix_spawn(out int pid, string path, IntPtr fileActions, IntPtr attributes, string?[] argv, string?[] envp);

        [DllImport("libc", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int signal);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        /// <summary>The library's own platform connection, with the count of its platform kill step.</summary>
        private sealed class CountingConnection : IDisposable
        {
            private readonly Func<int> kills;

            private CountingConnection(Unix.PtyConnection connection, Func<int> kills)
            {
                this.Connection = connection;
                this.kills = kills;
            }

            public Unix.PtyConnection Connection { get; }

            public int PlatformKills => this.kills();

            public static CountingConnection Create(int controller, int pid, bool useAsyncIo, bool forward)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var mac = new CountingMacConnection(controller, pid, useAsyncIo, forward);
                    return new CountingConnection(mac, () => mac.Kills);
                }

                var linux = new CountingLinuxConnection(controller, pid, useAsyncIo, forward);
                return new CountingConnection(linux, () => linux.Kills);
            }

            public void Dispose() => this.Connection.Dispose();
        }

        /// <summary>
        /// Counts each entry into the platform kill step; when <c>forward</c> is false it sends nothing, so a
        /// call that the library should not have made cannot reach a reused pid on the developer's machine.
        /// </summary>
        private sealed class CountingMacConnection : Mac.PtyConnection
        {
            private readonly bool forward;
            private int kills;

            public CountingMacConnection(int controller, int pid, bool useAsyncIo, bool forward)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
            }

            public int Kills => Volatile.Read(ref this.kills);

            protected override bool Kill(int controller)
            {
                Interlocked.Increment(ref this.kills);
                return !this.forward || base.Kill(controller);
            }
        }

        /// <inheritdoc cref="CountingMacConnection"/>
        private sealed class CountingLinuxConnection : Linux.PtyConnection
        {
            private readonly bool forward;
            private int kills;

            public CountingLinuxConnection(int controller, int pid, bool useAsyncIo, bool forward)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
            }

            public int Kills => Volatile.Read(ref this.kills);

            protected override bool Kill(int controller)
            {
                Interlocked.Increment(ref this.kills);
                return !this.forward || base.Kill(controller);
            }
        }
    }
}
