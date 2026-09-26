using CosmicWin.App.Alerts;

namespace CosmicWin.App.Tests.Alerts;

/// <summary>
/// T9a (webview-alert-layer): the exact wording <see cref="AlertLayerTrace"/> produces for every
/// alert-layer lifecycle event -- factored out of <c>WebViewAlertLayerController</c> so the format
/// itself is unit-testable without a real WebView2. That controller's own async creation path stays
/// a hardware-only concern (see its remarks and T3/T6's progress notes); these tests prove the
/// WORDING every call site hands to <c>desktopTrace</c>, not that the call sites fire on hardware.
/// </summary>
public sealed class AlertLayerTraceTests
{
    [Fact]
    public void CreateStart_NamesHwndAndGeneration() =>
        Assert.Equal("alert-layer create start hwnd=0x2A generation=3", AlertLayerTrace.CreateStart(0x2A, 3));

    [Fact]
    public void EnvironmentReady_NamesElapsedMilliseconds() =>
        Assert.Equal("alert-layer environment ready 150ms", AlertLayerTrace.EnvironmentReady(150));

    [Fact]
    public void ControllerReady_NamesElapsedMilliseconds() =>
        Assert.Equal("alert-layer controller ready 280ms", AlertLayerTrace.ControllerReady(280));

    [Fact]
    public void NavigationCompleted_NamesSuccessStatusAndElapsed()
    {
        Assert.Equal("alert-layer navigation completed success=True status=Unknown 42ms",
            AlertLayerTrace.NavigationCompleted(true, "Unknown", 42));
        Assert.Equal("alert-layer navigation completed success=False status=ConnectionAborted 42ms",
            AlertLayerTrace.NavigationCompleted(false, "ConnectionAborted", 42));
    }

    /// <summary>Columns and rows are deliberately DIFFERENT (4x2, not a symmetric grid) so a columns/rows swap in the format string would fail this.</summary>
    [Fact]
    public void Show_NamesTilesGridGapAndDuration() =>
        Assert.Equal(
            "alert-layer show tiles=failed,failed,warning grid=4x2 gap=8 duration=5000",
            AlertLayerTrace.Show(new AlertShowRequest(["failed", "failed", "warning"], 4, 2, 8, 5000)));

    [Fact]
    public void Show_NamesASingleTileWithoutATrailingComma() =>
        Assert.Equal(
            "alert-layer show tiles=warning grid=1x1 gap=0 duration=1000",
            AlertLayerTrace.Show(new AlertShowRequest(["warning"], 1, 1, 0, 1000)));

    [Fact]
    public void Hide_IsAFixedLine() => Assert.Equal("alert-layer hide", AlertLayerTrace.Hide());

    [Fact]
    public void PageReady_IsAFixedLine() => Assert.Equal("alert-layer page ready", AlertLayerTrace.PageReady());

    /// <summary>Columns and rows are deliberately DIFFERENT (3x2, not a symmetric grid) so a columns/rows swap in the format string would fail this.</summary>
    [Fact]
    public void PendingShowApplied_NamesTilesGridGapAndRemainingDuration() =>
        Assert.Equal(
            "alert-layer pending show applied tiles=failed,failed,warning,warning,warning grid=3x2 gap=8 remaining=17500",
            AlertLayerTrace.PendingShowApplied(
                new AlertShowRequest(["failed", "failed", "warning", "warning", "warning"], 3, 2, 8, 17500)));

    [Fact]
    public void Done_IsAFixedLine() => Assert.Equal("alert-layer done", AlertLayerTrace.Done());

    [Fact]
    public void Close_NamesTheReason() =>
        Assert.Equal("alert-layer close reason=host-changed", AlertLayerTrace.Close("host-changed"));

    [Fact]
    public void ProcessFailed_NamesKindAndReason() =>
        Assert.Equal("alert-layer process failed kind=BrowserProcessExited reason=Crashed",
            AlertLayerTrace.ProcessFailed("BrowserProcessExited", "Crashed"));

    [Fact]
    public void Error_NamesContextAndExceptionTypeAndMessage() =>
        Assert.Equal("alert-layer error poll: InvalidOperationException: boom",
            AlertLayerTrace.Error("poll", new InvalidOperationException("boom")));

    [Fact]
    public void NoOverlayVisual_IsAFixedLine() =>
        Assert.Equal("alert-layer create failed: no overlay visual", AlertLayerTrace.NoOverlayVisual());
}
