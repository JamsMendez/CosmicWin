using System.Diagnostics;
using CosmicWin.App.Tests.Alerts;

namespace CosmicWin.App.Tests.Wallpaper;

/// <summary>
/// html-wallpaper-demo D2: the only thing that actually EXERCISES the shipped processing-scene
/// wallpaper page (<c>CosmicWin.App/Wallpaper/Web/processing/</c>) instead of just checking the files
/// exist -- modelled on <see cref="Alerts.AlertLayerLayoutNodeTests"/>, which proves the same kind of
/// thing for the single-file <c>alert-layer.js</c>.
/// </summary>
/// <remarks>
/// Loads the REAL shipped page directory (copied to the test output next to the executable, same
/// <c>CopyToOutputDirectory</c> mechanism <c>CosmicWin.App.csproj</c> already uses for
/// <c>Alerts\Web\**</c>) into a Node <c>vm</c> sandbox and drives it from
/// <c>processing-scene.tests.js</c> (<c>CosmicWin.App.Tests/Wallpaper/Web/</c>, copied to the build
/// output the same way as <c>alert-layer-layout.tests.js</c>). Reuses <see
/// cref="NodeAvailability"/>/<see cref="RequiresNodeFactAttribute"/> from
/// <c>CosmicWin.App.Tests.Alerts</c> rather than duplicating the "gate, never fake" node-on-PATH
/// check.
/// </remarks>
public sealed class ProcessingSceneNodeTests
{
    private static readonly string ProcessingSceneDirectory =
        Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web", "processing");

    private static readonly string HarnessScriptPath =
        Path.Combine(AppContext.BaseDirectory, "Wallpaper", "Web", "processing-scene.tests.js");

    /// <summary>
    /// Generous, but bounded -- the harness runs a handful of small vm-sandboxed cases, including one
    /// full frame of the entire scene's render() pipeline, and normally finishes in well under a
    /// second; this only exists to turn "the process manager wedged" into a reported test failure
    /// instead of a `dotnet test` run that never comes back (see
    /// <see cref="Alerts.AlertLayerLayoutNodeTests.HarnessTimeout"/>, the same shape).
    /// </summary>
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromSeconds(30);

    [RequiresNodeFact]
    public void ProcessingSceneHarness_PassesAgainstTheRealShippedPage()
    {
        Assert.True(Directory.Exists(ProcessingSceneDirectory),
            $"Expected '{ProcessingSceneDirectory}' to exist (shipped content, see CosmicWin.App.csproj's Wallpaper\\Web\\** Content item).");
        Assert.True(File.Exists(Path.Combine(ProcessingSceneDirectory, "js", "alert-overlay.js")),
            $"Expected the shipped alert-overlay.js under '{ProcessingSceneDirectory}'.");
        Assert.True(File.Exists(HarnessScriptPath),
            $"Expected '{HarnessScriptPath}' to exist (test content, see this project's .csproj).");

        var result = RunNode(HarnessTimeout, HarnessScriptPath, ProcessingSceneDirectory);

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

        // Read BOTH streams CONCURRENTLY -- see AlertLayerLayoutNodeTests.RunNode's own remarks on
        // why a sequential read here would risk a full-pipe deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return (-1, stdoutTask.Result, stderrTask.Result, TimedOut: true, process.Id);
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result, TimedOut: false, process.Id);
    }
}
