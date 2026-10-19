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
