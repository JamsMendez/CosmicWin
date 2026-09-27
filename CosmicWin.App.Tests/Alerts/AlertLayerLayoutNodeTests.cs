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
        TimeSpan timeout, params string[] arguments)
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

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            // Kill the WHOLE tree, not just this process: node can have spawned children of its own,
            // and an orphan left running (holding the pipes open) would also keep the two read tasks
            // above from ever completing.
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return (-1, stdoutTask.Result, stderrTask.Result, TimedOut: true, process.Id);
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false, process.Id);
    }
}
