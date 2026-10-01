using System.Diagnostics;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T9 (alert-tile-mosaic, review follow-ups R3-js-mosaic-behavior-unproved and
/// R3-js-workarea-layout-only-structurally-guarded): the only thing that actually EXERCISES
/// <c>alert-layer.js</c>'s tile-layout math (<c>tileRects</c>/<c>gridAreaRect</c>) instead of
/// grepping for its presence, closing the gap <see cref="AlertLayerWebPageTests"/>'s own remarks
/// name honestly ("no DOM/canvas test harness exists in this repo").
/// </summary>
/// <remarks>
/// Loads the REAL shipped page -- the exact file <see cref="AlertLayerWebPageTests"/> already reads
/// from <see cref="AppContext.BaseDirectory"/> -- into a Node <c>vm</c> sandbox and drives it from
/// <c>alert-layer-layout.tests.js</c> (<c>CosmicWin.App.Tests/Alerts/Web/</c>, copied to the build
/// output the same way as the page itself, see this project's <c>.csproj</c>). No .NET JS engine or
/// headless browser exists in this repo (feature doc, "JS has no test harness"), so a Node
/// subprocess is the smallest thing that can run the real file's own <c>vm</c>-sandboxable code with
/// zero framework changes -- top-level <c>function</c>/<c>var</c> declarations in a script run via
/// <c>vm.runInContext</c> already become properties of the sandbox object, so nothing in
/// <c>alert-layer.js</c> needed an export/module system added to be reachable this way.
/// <para>
/// Skips (never fails) when <c>node</c> is not on PATH, via <see cref="RequiresNodeFactAttribute"/>
/// -- the same "gate, never fake" shape <c>DesktopFactAttributes</c> already uses for the desktop
/// opt-in gates.
/// </para>
/// </remarks>
public sealed class AlertLayerLayoutNodeTests
{
    private static readonly string AlertLayerJsPath =
        Path.Combine(AppContext.BaseDirectory, "Alerts", "Web", "alert-layer.js");

    private static readonly string HarnessScriptPath =
        Path.Combine(AppContext.BaseDirectory, "Alerts", "Web", "alert-layer-layout.tests.js");

    /// <summary>
    /// T14 (alert-tile-mosaic, review R3/R4-node-harness-no-timeout): generous, but bounded -- the
    /// harness runs 13 small vm-sandboxed cases and normally finishes in well under a second; this
    /// only exists to turn "the process manager wedged" into a reported test failure instead of a
    /// `dotnet test` run that never comes back.
    /// </summary>
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(30);

    [RequiresNodeFact]
    public void TileLayoutHarness_PassesAgainstTheRealShippedPage()
    {
        Assert.True(File.Exists(AlertLayerJsPath), $"Expected '{AlertLayerJsPath}' to exist (shipped content, see AlertLayerWebPageTests).");
        Assert.True(File.Exists(HarnessScriptPath), $"Expected '{HarnessScriptPath}' to exist (test content, see this project's .csproj).");

        var result = RunNode(HarnessTimeout, HarnessScriptPath, AlertLayerJsPath);

        Assert.False(
            result.TimedOut,
            $"Node harness did not exit within {HarnessTimeout} and was killed (process tree).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
        Assert.True(
            result.ExitCode == 0,
            $"Node harness failed (exit {result.ExitCode}).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{result.Stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{result.Stderr}");
    }

    /// <summary>
    /// T14 (alert-tile-mosaic, review R3/R4-node-harness-no-timeout, R3-node-probe-leaks-on-timeout):
    /// cheap proof of the timeout path that does not wait anywhere near <see cref="HarnessTimeout"/>
    /// -- a process that never exits on its own (a bare <c>setInterval</c>, nothing calls
    /// <c>unref</c>) run against a tiny bound, asserting it is both REPORTED as timed out and
    /// actually KILLED (the whole tree, not left running in the background).
    /// </summary>
    [RequiresNodeFact]
    public void RunNode_AProcessThatNeverExits_IsKilledAndReportedAsTimedOut()
    {
        var result = RunNode(TimeSpan.FromMilliseconds(200), "-e", "setInterval(() => {}, 1000)");

        Assert.True(result.TimedOut, "Expected the hung node process to be reported as timed out.");
        Assert.False(
            ProcessStillRunning(result.ProcessId),
            "Expected the timed-out node process to actually be killed, not just reported.");
    }

    /// <summary>
    /// R3-timeout-test-proves-root-only: the test above only spawns one process, so it cannot tell
    /// <c>Kill(entireProcessTree: true)</c> from a plain <c>Kill()</c>. Here the hung script spawns a
    /// CHILD of its own and reports the child's pid; after the timeout kill the child must be gone too.
    /// </summary>
    [RequiresNodeFact]
    public void RunNode_TimeoutKill_TakesTheChildrenOfTheHungScriptToo()
    {
        using var scratch = new ScratchDirectory();
        var pidFile = Path.Combine(scratch.Path, "child.pid");
        var script = scratch.Write("parent.js", """
            const { spawn } = require('child_process');
            const fs = require('fs');
            const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore', detached: true });
            fs.writeFileSync(process.argv[2], String(child.pid));
            setInterval(() => {}, 1000);
            """);
        int childPid = 0;
        try
        {
            var result = RunNodeOnceReady(TimeSpan.FromSeconds(3), pidFile, script, pidFile);
            childPid = ReadPid(pidFile);

            Assert.True(result.TimedOut, "Expected the hung parent script to be reported as timed out.");
            Assert.True(childPid > 0, "Expected the hung script to have reported its child's pid before the timeout.");
            Assert.False(
                ProcessStillRunning(childPid),
                "Expected the timeout kill to take the script's child process down with it (entireProcessTree).");
        }
        finally
        {
            KillIfRunning(childPid);
        }
    }

    /// <summary>
    /// R3/R4-runnode-timeout-branch-unbounded-result: a descendant that outlives the kill keeps the
    /// redirected pipes open, and the old <c>Task.Result</c> reads after the kill then blocked forever.
    /// On Windows a real tree kill reaches even orphaned grandchildren, so the survivor is produced by
    /// injecting a root-only kill (the "kill missed a holder" case) instead of a tree kill.
    /// </summary>
    [RequiresNodeFact]
    public void RunNode_TimeoutKill_DoesNotHangWhenASurvivorStillHoldsThePipes()
    {
        using var scratch = new ScratchDirectory();
        var pidFile = Path.Combine(scratch.Path, "survivor.pid");
        var script = scratch.Write("parent.js", """
            const { spawn } = require('child_process');
            const fs = require('fs');
            const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'inherit', detached: true });
            fs.writeFileSync(process.argv[2], String(child.pid));
            setInterval(() => {}, 1000);
            """);
        int survivorPid = 0;
        try
        {
            var run = Task.Run(() => RunNode(
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), killEntireProcessTree: false, readyFile: pidFile,
                script, pidFile));
            var finished = run.Wait(TimeSpan.FromSeconds(20));
            survivorPid = ReadPid(pidFile);

            Assert.True(finished, "Expected RunNode to return after the timeout kill even though a survivor still holds the pipes.");
            Assert.True(run.Result.TimedOut);
            Assert.True(survivorPid > 0 && ProcessStillRunning(survivorPid), "Test setup: the survivor should still be running (root-only kill).");
        }
        finally
        {
            // Releases the pipes too, so a RunNode that was (wrongly) still blocked can finish.
            KillIfRunning(survivorPid);
        }
    }

    /// <summary>
    /// R3-probe-timeout-path-unproved: aims the probe at a process that never exits, with a tiny bound,
    /// and requires both the "unavailable" answer and that the probe's process was actually killed.
    /// </summary>
    [RequiresNodeFact]
    public void NodeAvailabilityProbe_AProcessThatNeverExits_IsKilledAndReportedUnavailable()
    {
        using var scratch = new ScratchDirectory();
        var pidFile = Path.Combine(scratch.Path, "probe.pid");
        var script = scratch.Write("probe.js", """
            require('fs').writeFileSync(process.argv[2], String(process.pid));
            setInterval(() => {}, 1000);
            """);
        int probePid = 0;
        try
        {
            var available = NodeAvailability.TryRunProbe(
                "node", [script, pidFile], TimeSpan.FromSeconds(3), readyFile: pidFile);
            probePid = ReadPid(pidFile);

            Assert.False(available, "Expected a probe that outlives its bound to report node as unavailable.");
            Assert.True(probePid > 0, "Expected the probe script to have reported its pid before the timeout.");
            Assert.False(ProcessStillRunning(probePid), "Expected the timed-out probe process to be killed.");
        }
        finally
        {
            KillIfRunning(probePid);
        }
    }

    /// <summary>
    /// R3-pid-handshake-shares-timeout-budget: the hang bound must not also pay for Node's own start.
    /// The script sleeps longer than the bound BEFORE it reports its pid, standing in for a slow cold
    /// start; with a ready file the bound only starts once the pid is on disk, so the pid is there.
    /// </summary>
    [RequiresNodeFact]
    public void RunNode_WithAReadyFile_StartsTheTimeoutOnlyOnceTheScriptIsReady()
    {
        using var scratch = new ScratchDirectory();
        var pidFile = Path.Combine(scratch.Path, "slow.pid");
        var script = scratch.Write("slow.js", SlowStartScript);
        int pid = 0;
        try
        {
            var result = RunNodeOnceReady(TimeSpan.FromMilliseconds(500), pidFile, script, pidFile);
            pid = ReadPid(pidFile);

            Assert.True(result.TimedOut);
            Assert.True(pid > 0, "Expected the timeout to start only after the slow script reported its pid.");
            Assert.False(ProcessStillRunning(pid), "Expected the slow script to be killed once its bound ran out.");
        }
        finally
        {
            KillIfRunning(pid);
        }
    }

    /// <summary>R3-pid-handshake-shares-timeout-budget: the same ready-file gate on the probe seam.</summary>
    [RequiresNodeFact]
    public void NodeAvailabilityProbe_WithAReadyFile_StartsTheTimeoutOnlyOnceTheScriptIsReady()
    {
        using var scratch = new ScratchDirectory();
        var pidFile = Path.Combine(scratch.Path, "slow-probe.pid");
        var script = scratch.Write("slow-probe.js", SlowStartScript);
        int pid = 0;
        try
        {
            var available = NodeAvailability.TryRunProbe(
                "node", [script, pidFile], TimeSpan.FromMilliseconds(500), readyFile: pidFile);
            pid = ReadPid(pidFile);

            Assert.False(available);
            Assert.True(pid > 0, "Expected the probe's timeout to start only after the slow script reported its pid.");
            Assert.False(ProcessStillRunning(pid), "Expected the timed-out probe process to be killed.");
        }
        finally
        {
            KillIfRunning(pid);
        }
    }

    // Writes its pid only after 1.5 s -- three times the 500 ms bound the tests above give it -- then hangs.
    private const string SlowStartScript = """
        setTimeout(() => {
          require('fs').writeFileSync(process.argv[2], String(process.pid));
          setInterval(() => {}, 1000);
        }, 1500);
        """;

    private static int ReadPid(string pidFile)
    {
        // With a ready file the run only times out after the pid is on disk, so it is normally there
        // already; the short poll only absorbs a slow disk.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile), out var pid))
            {
                return pid;
            }

            Thread.Sleep(50);
        }

        return 0;
    }

    private static void KillIfRunning(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Already gone -- the outcome the cleanup wants.
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cosmicwin-nodetests-" + Guid.NewGuid().ToString("N"));

        public ScratchDirectory() => Directory.CreateDirectory(Path);

        public string Write(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static bool ProcessStillRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id -- exactly what "actually killed" means here.
            return false;
        }
    }

    private static (int ExitCode, string Stdout, string Stderr, bool TimedOut, int ProcessId) RunNode(
        TimeSpan timeout, params string[] arguments) =>
        RunNode(timeout, TimeSpan.FromSeconds(5), killEntireProcessTree: true, readyFile: null, arguments);

    // A distinct NAME, not a RunNode overload: with `params string[]` an overload taking a string
    // readyFile would silently capture the first script argument of every plain RunNode call.
    private static (int ExitCode, string Stdout, string Stderr, bool TimedOut, int ProcessId) RunNodeOnceReady(
        TimeSpan timeout, string readyFile, params string[] arguments) =>
        RunNode(timeout, TimeSpan.FromSeconds(5), killEntireProcessTree: true, readyFile, arguments);

    private static (int ExitCode, string Stdout, string Stderr, bool TimedOut, int ProcessId) RunNode(
        TimeSpan timeout, TimeSpan drainTimeout, bool killEntireProcessTree, string? readyFile,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start `node` -- RequiresNodeFact should have skipped this fact instead.");

        // Read BOTH streams CONCURRENTLY, never one after the other: a script that writes enough to
        // stderr while stdout is still being drained here (or vice versa) fills that pipe's OS buffer,
        // and a full pipe nobody is reading blocks the CHILD forever -- a deadlock inside `node`, not
        // a hang in this method, but indistinguishable from one without this.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        NodeAvailability.WaitUntilReady(process, readyFile);
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            // Kill the WHOLE tree, not just this process: node can have spawned children of its own,
            // and an orphan left running (holding the pipes open) would also keep the two read tasks
            // above from ever completing. killEntireProcessTree is true for every real run; only the
            // drain test passes false, to leave a survivor holding the pipes on purpose.
            process.Kill(killEntireProcessTree);
            process.WaitForExit(5000);

            // R3/R4-runnode-timeout-branch-unbounded-result: the tree kill above can still miss a
            // descendant that keeps the redirected pipes open, and an unbounded Task.Result on a pipe
            // nobody will ever close would hang the whole test run. Wait a bounded time for the streams
            // and report whatever completed (empty when it did not) -- the timeout itself is the failure.
            Task.WaitAll([stdoutTask, stderrTask], drainTimeout);
            return (-1, CompletedOrEmpty(stdoutTask), CompletedOrEmpty(stderrTask), TimedOut: true, process.Id);
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false, process.Id);
    }

    private static string CompletedOrEmpty(Task<string> readTask) =>
        readTask.IsCompletedSuccessfully ? readTask.Result : string.Empty;
}
