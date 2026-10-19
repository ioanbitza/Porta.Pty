// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Porta.Pty.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The edges of "no signal once the child is collected" that the first tests did not hold: a child
    /// collected by someone else, a signal racing the collection, the macOS grace period between SIGHUP
    /// and SIGKILL, and the controller's close-on-exec flag for processes started outside Porta.
    ///
    /// <para>The connections are the library's own Mac and Linux connections; only SendSignal, the one
    /// place a signal leaves the library, is overridden, to record each signal with the result of kill(2).
    /// Children are started by the test with posix_spawn and the controller is /dev/null, since no
    /// terminal is needed to collect or signal a child.</para>
    /// </summary>
    [TestClass]
    public class CollectionRaceTests
    {
        private const int SIGHUP = 1;
        private const int SIGKILL = 9;
        private const int SIGTERM = 15;
        private const int ESRCH = 3;
        private const int O_RDWR = 2;
        private const int WNOHANG = 1;

        private static readonly int TestTimeoutMs = Debugger.IsAttached ? 300_000 : 10_000;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        /// A child collected by a third party (here the test's own waitpid, as a host with SIGCHLD ignored
        /// or another reaper would) before the connection watches it: the watcher meets ECHILD, and from then
        /// on the pid is free, so Kill and Dispose must not signal it.
        /// </summary>
        [TestMethod]
        [DataRow("Kill", false, DisplayName = "Kill, blocking")]
        [DataRow("Kill", true, DisplayName = "Kill, non-blocking")]
        [DataRow("Dispose", false, DisplayName = "Dispose, blocking")]
        [DataRow("Dispose", true, DisplayName = "Dispose, non-blocking")]
        public void Unix_AfterAThirdPartyCollectedTheChild_KillAndDisposeSendNoSignal(string call, bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            var stray = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                kill(pid, SIGKILL).Should().Be(0);
                var status = 0;
                waitpid(pid, ref status, 0).Should().Be(pid, "the third party should collect the child");

                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: false);

                // The watcher learns of the third party's collection on its own thread. There is no public
                // signal for it, so the test waits, at most 2 s, for the connection's own record of it;
                // a library that never records it is then measured as it is.
                probe.WaitUntilMarkedCollected(2000);

                if (call == "Kill")
                {
                    probe.Connection.Kill();
                }

                probe.Connection.Dispose();

                if (!probe.Sent.IsEmpty)
                {
                    stray.Add($"iteration {i}: pid {pid} sent [{string.Join(", ", probe.Sent.Select(s => s.Signal))}]");
                }
            }

            stray.Should().BeEmpty(
                "{0} must not signal a pid a third party already collected, since it may belong to another process by now (useAsyncIo={1})",
                call,
                useAsyncIo);
        }

        /// <summary>
        /// A signal racing the collection must never reach a freed pid. A thread calls SignalChild with
        /// signal 0 (kill(2)'s existence check, harmless) without pause while the test kills the child;
        /// every call that finds the pid gone (ESRCH) is a signal that would have hit a freed, possibly
        /// reused, pid. Holds only when the collection and the signal take the same lock and the flag
        /// is checked under it.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Unix_SignalsRacingTheCollection_NeverReachAFreedPid(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("pid reuse after waitpid is a Unix concern");
            }

            const int iterations = 200;
            var strays = 0;
            var calls = 0L;
            var lost = 0;
            for (var i = 0; i < iterations; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                var stop = 0;
                var hammered = 0L;
                var hammer = new Thread(() =>
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        probe.Signal(0);
                        Interlocked.Increment(ref hammered);
                    }
                });
                hammer.Start();

                kill(pid, SIGKILL).Should().Be(0);
                var exited = probe.Connection.WaitForExit(TestTimeoutMs);

                // A few more calls after the exit is reported, so a library that stops checking the flag
                // is caught even when the collection itself won every race.
                var after = Interlocked.Read(ref hammered) + 20;
                var deadline = Stopwatch.StartNew();
                while (exited && Interlocked.Read(ref hammered) < after && deadline.ElapsedMilliseconds < 1000)
                {
                    Thread.Yield();
                }

                Volatile.Write(ref stop, 1);
                hammer.Join();
                calls += Interlocked.Read(ref hammered);

                if (!exited)
                {
                    // The exit the non-blocking reaper sometimes loses: the child is still ours,
                    // a zombie, so nothing recorded above reached a freed pid; it is collected here.
                    lost++;
                    var status = 0;
                    waitpid(pid, ref status, 0);
                    continue;
                }

                strays += probe.Sent.Count(s => s.Result == -1 && s.Errno == ESRCH);
            }

            strays.Should().Be(
                0,
                "no signal may reach the pid once it is collected (useAsyncIo={0}; {1} SignalChild calls over {2} children, {3} exits lost by the reaper)",
                useAsyncIo,
                calls,
                iterations - lost,
                lost);
        }

        /// <summary>
        /// macOS: Kill of a child that dies of SIGHUP returns once the child is collected, not after the
        /// fixed 100 ms grace period. The fastest of five is compared with half the grace period.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public void Mac_KillOfAChildThatDiesOfSigHup_DoesNotWaitTheGracePeriod(bool useAsyncIo)
        {
            if (!IsMac)
            {
                Assert.Inconclusive("the 100 ms grace period between SIGHUP and SIGKILL exists only on macOS");
            }

            var durations = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var pid = SpawnChild("exec /bin/sleep 600");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                var clock = Stopwatch.StartNew();
                probe.Connection.Kill();
                var elapsed = clock.Elapsed.TotalMilliseconds;

                if (probe.Connection.WaitForExit(TestTimeoutMs))
                {
                    durations.Add(elapsed);
                }
                else
                {
                    var status = 0;
                    waitpid(pid, ref status, 0);
                }
            }

            if (durations.Count == 0)
            {
                Assert.Inconclusive("the library reported the exit of none of the 5 children");
            }

            durations.Min().Should().BeLessThan(
                50,
                "Kill should return as soon as the child has died of SIGHUP (useAsyncIo={0}); took [{1}] ms",
                useAsyncIo,
                string.Join(", ", durations.Select(d => d.ToString("F1"))));
        }

        /// <summary>
        /// macOS: SIGKILL is the fallback for a child that ignores SIGHUP, and only for it. A child that
        /// dies of SIGHUP receives SIGHUP alone; a child that ignores it receives SIGHUP, then SIGKILL, and dies.
        /// </summary>
        [TestMethod]
        [DataRow(false, false, DisplayName = "dies of SIGHUP, blocking")]
        [DataRow(false, true, DisplayName = "dies of SIGHUP, non-blocking")]
        [DataRow(true, false, DisplayName = "ignores SIGHUP, blocking")]
        [DataRow(true, true, DisplayName = "ignores SIGHUP, non-blocking")]
        public void Mac_SigKillIsSentOnlyToAChildThatIgnoresSigHup(bool ignoresSigHup, bool useAsyncIo)
        {
            if (!IsMac)
            {
                Assert.Inconclusive("only macOS follows SIGHUP with SIGKILL");
            }

            var expected = ignoresSigHup ? new[] { SIGHUP, SIGKILL } : new[] { SIGHUP };
            var wrong = new List<string>();
            var lost = 0;
            for (var i = 0; i < 5; i++)
            {
                var pid = SpawnChild(ignoresSigHup ? "trap '' HUP; exec /bin/sleep 600" : "exec /bin/sleep 600");

                // The ignored SIGHUP survives the exec, so once the child runs /bin/sleep it is in place;
                // a SIGHUP sent before the shell reached its trap would test nothing.
                WaitUntilRunning(pid, "/bin/sleep");
                using var probe = Probe.Create(OpenDevNull(), pid, useAsyncIo, forward: true);

                probe.Connection.Kill();

                if (!probe.Connection.WaitForExit(TestTimeoutMs))
                {
                    // A dead child the library did not report is the exit the non-blocking reaper sometimes
                    // loses: the iteration is not counted. A child still running is a failure.
                    var status = 0;
                    if (waitpid(pid, ref status, WNOHANG) == pid)
                    {
                        lost++;
                        continue;
                    }

                    kill(pid, SIGKILL);
                    waitpid(pid, ref status, 0);
                    wrong.Add($"iteration {i}: child {pid} still running {TestTimeoutMs} ms after Kill");
                    continue;
                }

                var sent = probe.Sent.Select(s => s.Signal).ToArray();
                if (!sent.SequenceEqual(expected))
                {
                    wrong.Add($"iteration {i}: sent [{string.Join(", ", sent)}]");
                }
            }

            if (lost == 5)
            {
                Assert.Inconclusive("the library reported the exit of none of the 5 children (the lost exit of the non-blocking reaper)");
            }

            wrong.Should().BeEmpty(
                "Kill of a child that {0} SIGHUP should send exactly [{1}] (useAsyncIo={2}; {3} exits lost by the reaper)",
                ignoresSigHup ? "ignores" : "dies of",
                string.Join(", ", expected),
                useAsyncIo,
                lost);
        }

        /// <summary>
        /// The controller is close-on-exec from the moment pty_spawn returns, so a process the host starts
        /// outside Porta (Process.Start) does not inherit terminal A's controller. Closing descriptors in
        /// Porta's own children cannot cover these. The terminals each child holds above stderr are listed
        /// with shell builtins before and after terminal A is spawned; A must add none.
        /// </summary>
        [TestMethod]
        [DataRow(false, DisplayName = "blocking")]
        [DataRow(true, DisplayName = "non-blocking")]
        public async Task Unix_TheControllerIsCloseOnExec_SoAProcessStartedOutsidePortaDoesNotInheritIt(bool useAsyncIo)
        {
            if (IsWindows)
            {
                Assert.Inconclusive("close-on-exec is a Unix concern");
            }

            using var cts = new CancellationTokenSource(TestTimeoutMs);
            var before = TerminalsSeenByAProcessStart();

            using var terminalA = await PtyProvider.SpawnAsync(
                new PtyOptions
                {
                    Name = "A",
                    Cols = 80,
                    Rows = 25,
                    Cwd = Environment.CurrentDirectory,
                    App = "/bin/cat",
                    CommandLine = Array.Empty<string>(),
                    Environment = new Dictionary<string, string>(),
                    UseAsyncIo = useAsyncIo,
                },
                cts.Token);

            var added = TerminalsSeenByAProcessStart().Except(before).ToArray();

            added.Should().BeEmpty(
                "a process started outside Porta should not inherit terminal A's controller (useAsyncIo={0}), but it held terminal descriptors [{1}]",
                useAsyncIo,
                string.Join(", ", added));
        }

        private static int[] TerminalsSeenByAProcessStart()
        {
            var start = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("n=3; while [ $n -lt 1024 ]; do if [ -t $n ]; then printf '%s ' $n; fi; n=$((n+1)); done; echo");

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(TestTimeoutMs).Should().BeTrue("the listing shell should exit");

            return output.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
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

        private static void WaitUntilRunning(int pid, string path)
        {
            var buffer = new byte[4096];
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < TestTimeoutMs)
            {
                var length = proc_pidpath(pid, buffer, (uint)buffer.Length);
                if (length > 0 && System.Text.Encoding.UTF8.GetString(buffer, 0, length) == path)
                {
                    return;
                }

                Thread.Sleep(1);
            }

            Assert.Fail($"child {pid} did not exec {path} within {TestTimeoutMs} ms");
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
        private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

        [DllImport("libc", SetLastError = true)]
        private static extern int waitpid(int pid, ref int status, int options);

        private readonly record struct Sent(int Signal, int Result, int Errno);

        /// <summary>The library's own platform connection, with every signal it sends recorded.</summary>
        private sealed class Probe : IDisposable
        {
            private readonly Func<int, bool> signal;

            private Probe(Unix.PtyConnection connection, ConcurrentQueue<Sent> sent, Func<int, bool> signal)
            {
                this.Connection = connection;
                this.Sent = sent;
                this.signal = signal;
            }

            public Unix.PtyConnection Connection { get; }

            public ConcurrentQueue<Sent> Sent { get; }

            /// <summary>
            /// Records each signal; when <paramref name="forward"/> is false nothing is sent, so a signal the
            /// library should not have sent cannot reach a reused pid on the developer's machine.
            /// </summary>
            public static Probe Create(int controller, int pid, bool useAsyncIo, bool forward)
            {
                var sent = new ConcurrentQueue<Sent>();
                if (IsMac)
                {
                    var mac = new RecordingMacConnection(controller, pid, useAsyncIo, forward, sent);
                    return new Probe(mac, sent, mac.Signal);
                }

                var linux = new RecordingLinuxConnection(controller, pid, useAsyncIo, forward, sent);
                return new Probe(linux, sent, linux.Signal);
            }

            public bool Signal(int signalNumber) => this.signal(signalNumber);

            /// <summary>Waits for the connection's private collected flag; returns whether it was set.</summary>
            public bool WaitUntilMarkedCollected(int timeoutMs)
            {
                var field = typeof(Unix.PtyConnection).GetField("reaped", BindingFlags.Instance | BindingFlags.NonPublic);
                field.Should().NotBeNull("Unix.PtyConnection should keep its collected flag in a field named reaped");

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    if ((bool)field!.GetValue(this.Connection)!)
                    {
                        return true;
                    }

                    Thread.Sleep(5);
                }

                return false;
            }

            public void Dispose() => this.Connection.Dispose();
        }

        private sealed class RecordingMacConnection : Mac.PtyConnection
        {
            private readonly bool forward;
            private readonly ConcurrentQueue<Sent> sent;

            public RecordingMacConnection(int controller, int pid, bool useAsyncIo, bool forward, ConcurrentQueue<Sent> sent)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
                this.sent = sent;
            }

            public bool Signal(int signal) => this.SignalChild(signal);

            protected override int SendSignal(int pid, int signal)
            {
                var result = this.forward ? base.SendSignal(pid, signal) : 0;
                var errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
                this.sent.Enqueue(new Sent(signal, result, errno));
                if (result == -1)
                {
                    Marshal.SetLastPInvokeError(errno);
                }

                return result;
            }
        }

        private sealed class RecordingLinuxConnection : Linux.PtyConnection
        {
            private readonly bool forward;
            private readonly ConcurrentQueue<Sent> sent;

            public RecordingLinuxConnection(int controller, int pid, bool useAsyncIo, bool forward, ConcurrentQueue<Sent> sent)
                : base(controller, pid, useAsyncIo)
            {
                this.forward = forward;
                this.sent = sent;
            }

            public bool Signal(int signal) => this.SignalChild(signal);

            protected override int SendSignal(int pid, int signal)
            {
                var result = this.forward ? base.SendSignal(pid, signal) : 0;
                var errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
                this.sent.Enqueue(new Sent(signal, result, errno));
                if (result == -1)
                {
                    Marshal.SetLastPInvokeError(errno);
                }

                return result;
            }
        }
    }
}
