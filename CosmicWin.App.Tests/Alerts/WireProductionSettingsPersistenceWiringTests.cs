using System.Runtime.CompilerServices;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// S10 (wallpaper-scene-http-endpoint, R3-production-loadorcreate-wiring-untested): proves
/// <c>AppComposition.WireProduction</c> reads its one production <c>settings</c> value through
/// <see cref="SettingsFile.LoadOrCreate()"/> -- the S9 call that WRITES <c>settings.conf</c> on a
/// missing file -- rather than the side-effect-free <see cref="SettingsFile.Load()"/>.
/// </summary>
/// <remarks>
/// Same technique as <see cref="WireProductionHtmlWallpaperSettingsWiringTests"/>: <c>WireProduction</c>
/// constructs real Win32/WebView2 collaborators and is not exercised directly by any test in this
/// project, so a source-text check is what can be honestly claimed here, not a behavioural guarantee.
/// The review finding this pins (S8/S9 review, 2026-09-27) was that nothing actually proved
/// <c>WireProduction</c> calls <c>LoadOrCreate</c> rather than <c>Load</c> -- both compile, both
/// return a <see cref="Settings"/>, and only <c>LoadOrCreate</c> ever writes the file.
/// </remarks>
public sealed class WireProductionSettingsPersistenceWiringTests
{
    private static string ReadAppCompositionSource([CallerFilePath] string testFilePath = "")
    {
        var testProjectDir = Path.GetDirectoryName(testFilePath)!;
        var path = Path.GetFullPath(Path.Combine(testProjectDir, "..", "..", "CosmicWin.App", "AppComposition.cs"));
        return File.ReadAllText(path);
    }

    [Fact]
    public void WireProduction_ReadsSettingsThroughLoadOrCreateRatherThanLoad()
    {
        var source = ReadAppCompositionSource();

        var methodStart = source.IndexOf(
            "public static AppComposition WireProduction(Action shutdown)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "expected to find WireProduction's declaration in AppComposition.cs");

        // Anchored on the one `var settings = ...` assignment inside WireProduction -- there is
        // exactly one, ahead of `TreeArranger.Gap = settings.Gap`. `loadGap`'s own
        // `() => SettingsFile.Load().Gap` closure (a few lines further down, deliberately still
        // plain Load -- a Reload must not create or rewrite the file either) does NOT match this
        // `var settings = ` prefix, so it cannot make this assertion pass by accident.
        var settingsAssignmentStart = source.IndexOf("var settings = SettingsFile.", methodStart, StringComparison.Ordinal);
        Assert.True(settingsAssignmentStart >= 0,
            "expected a `var settings = SettingsFile...` assignment inside WireProduction");
        var lineEnd = source.IndexOf(';', settingsAssignmentStart);
        Assert.True(lineEnd > settingsAssignmentStart, "expected the settings assignment to end with a semicolon");
        var settingsAssignment = source[settingsAssignmentStart..(lineEnd + 1)];

        Assert.StartsWith("var settings = SettingsFile.LoadOrCreate(", settingsAssignment, StringComparison.Ordinal);
        Assert.DoesNotContain("var settings = SettingsFile.Load();", source, StringComparison.Ordinal);

        // loadGap's Reload closure must stay untouched by this task: still plain Load, not
        // LoadOrCreate -- a Reload is not a first run and must never create or rewrite the file.
        Assert.Contains("loadGap: () => SettingsFile.Load().Gap", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// S10 (wallpaper-scene-http-endpoint, R3-first-run-write-failure-silent): a failed settings
    /// write must reach <c>desktopTrace</c> from BOTH the first-run <c>LoadOrCreate</c> call AND
    /// every later persist through <see cref="SynchronizedSettingsStore"/>'s own save delegate --
    /// otherwise only one of the two paths would ever be observable.
    /// </summary>
    [Fact]
    public void WireProduction_WiresTheSameSaveFailureDiagnosticIntoLoadOrCreateAndTheSettingsStore()
    {
        var source = ReadAppCompositionSource();

        var methodStart = source.IndexOf(
            "public static AppComposition WireProduction(Action shutdown)", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "expected to find WireProduction's declaration in AppComposition.cs");

        Assert.Contains(
            "void OnSettingsSaveFailed(string errorType) =>", source[methodStart..], StringComparison.Ordinal);
        Assert.Contains(
            "SettingsFile.LoadOrCreate(onDiagnostic: OnSettingsSaveFailed)",
            source[methodStart..], StringComparison.Ordinal);
        // Anchored on the call's start/end rather than the exact line-wrapping in between, which
        // CRLF/formatting differences make fragile (same reasoning as
        // WireProductionHtmlWallpaperSettingsWiringTests' own closeMarker anchor).
        var storeCallStart = source.IndexOf(
            "new SynchronizedSettingsStore(", methodStart, StringComparison.Ordinal);
        Assert.True(storeCallStart >= 0, "expected a `new SynchronizedSettingsStore(...)` call inside WireProduction");
        var storeCallEnd = source.IndexOf(");", storeCallStart, StringComparison.Ordinal);
        Assert.True(storeCallEnd > storeCallStart, "expected the SynchronizedSettingsStore call to end with `);`");
        var storeCall = source[storeCallStart..(storeCallEnd + 2)];

        Assert.Contains("settings, s => SettingsFile.Save(s, OnSettingsSaveFailed)", storeCall, StringComparison.Ordinal);
    }
}
