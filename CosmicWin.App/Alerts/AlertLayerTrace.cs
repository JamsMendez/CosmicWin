namespace CosmicWin.App.Alerts;

/// <summary>
/// T9a (webview-alert-layer): the exact wording for every alert-layer trace line, factored out of
/// <see cref="WebViewAlertLayerController"/> so the FORMAT itself is unit-testable without a real
/// WebView2 -- see that class's remarks for why its own async creation path stays a hardware-only
/// concern (same limitation T3/T6 already documented). Every line is prefixed <c>"alert-layer "</c>
/// and handed to <c>desktopTrace</c> in production (see <c>AppComposition.WireProduction</c>), fixing
/// T6's F1/F2 blind spot: production had no navigation/render telemetry at all.
/// </summary>
internal static class AlertLayerTrace
{
    public static string CreateStart(nint hwnd, int generation) =>
        $"alert-layer create start hwnd=0x{hwnd:X} generation={generation}";

    public static string EnvironmentReady(long elapsedMilliseconds) =>
        $"alert-layer environment ready {elapsedMilliseconds}ms";

    public static string ControllerReady(long elapsedMilliseconds) =>
        $"alert-layer controller ready {elapsedMilliseconds}ms";

    /// <summary><paramref name="webErrorStatus"/> takes the enum's own <c>ToString()</c> so this class does not need a WebView2 reference.</summary>
    public static string NavigationCompleted(bool success, object webErrorStatus, long elapsedMilliseconds) =>
        $"alert-layer navigation completed success={success} status={webErrorStatus} {elapsedMilliseconds}ms";

    public static string Show(string kind, int durationMilliseconds) =>
        $"alert-layer show kind={kind} duration={durationMilliseconds}";

    public static string Hide() => "alert-layer hide";

    public static string Done() => "alert-layer done";

    public static string PageReady() => "alert-layer page ready";

    public static string PendingShowApplied(string kind, int remainingMilliseconds) =>
        $"alert-layer pending show applied kind={kind} remaining={remainingMilliseconds}";

    public static string Close(string reason) => $"alert-layer close reason={reason}";

    /// <summary><paramref name="kind"/>/<paramref name="reason"/> take the enums' own <c>ToString()</c>, same reason as <see cref="NavigationCompleted"/>.</summary>
    public static string ProcessFailed(object kind, object reason) =>
        $"alert-layer process failed kind={kind} reason={reason}";

    /// <summary>Every currently-silent <c>Debug.WriteLine</c> catch site gets one of these alongside it, so the failure is visible in production too, not only under a debugger.</summary>
    public static string Error(string context, Exception exception) =>
        $"alert-layer error {context}: {exception.GetType().Name}: {exception.Message}";

    public static string NoOverlayVisual() => "alert-layer create failed: no overlay visual";
}
