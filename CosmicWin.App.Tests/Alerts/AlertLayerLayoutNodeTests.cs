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

    [RequiresNodeFact]
    public void TileLayoutHarness_PassesAgainstTheRealShippedPage()
    {
        Assert.True(File.Exists(AlertLayerJsPath), $"Expected '{AlertLayerJsPath}' to exist (shipped content, see AlertLayerWebPageTests).");
        Assert.True(File.Exists(HarnessScriptPath), $"Expected '{HarnessScriptPath}' to exist (test content, see this project's .csproj).");

        var (exitCode, stdout, stderr) = RunNode(HarnessScriptPath, AlertLayerJsPath);

        Assert.True(
            exitCode == 0,
            $"Node harness failed (exit {exitCode}).{Environment.NewLine}" +
            $"--- stdout ---{Environment.NewLine}{stdout}{Environment.NewLine}" +
            $"--- stderr ---{Environment.NewLine}{stderr}");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunNode(params string[] arguments)
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
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
