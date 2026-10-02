using System.Text.Json;

namespace CosmicWin.App.Alerts;

/// <summary>
/// see-through-video-tint (S4): owns the per-show tint lifecycle between the alert page and an
/// <see cref="IAlertTintSink"/>. The WebView2 shell (<see cref="WebViewAlertLayerController"/>) only
/// forwards page messages here and calls <see cref="BeginShow"/>/<see cref="Clear"/> at its
/// lifecycle points, so the whole protocol is testable without a browser.
/// </summary>
/// <remarks>
/// <para>
/// Sequence ids: every <see cref="BeginShow"/> starts a new, never-reused id that the page echoes in
/// its mask message. A mask is applied only when its id is the ACTIVE one and no mask was applied for
/// it yet, so a late mask (after hide, done, a reload, or a newer show) can never tint a later alert.
/// <see cref="Clear"/> deactivates the id first, under the same lock as the sink calls, so a decode
/// that finishes after a clear cannot slip a <c>SetTint</c> in behind it.
/// </para>
/// <para>
/// Message wire format (page to host, a JSON string):
/// <c>{"type":"mask","seq":N,"kind":"failed"|"warning","width":W,"height":H,"png":"data:image/png;base64,..."}</c>.
/// Anything malformed is traced and ignored; nothing here throws into a WebView2 callback.
/// </para>
/// </remarks>
internal sealed class AlertTintCoordinator
{
    /// <summary>Longest page message accepted, in chars (a 6880x2880 flat-letter PNG is well under 1 MB).</summary>
    public const int MaxMessageChars = 24_000_000;

    private readonly IAlertTintSink _sink;
    private readonly Action<string> _postToPage;
    private readonly Action<string>? _trace;
    private readonly object _gate = new();
    private int _lastSeq;
    private int _activeSeq; // 0 = no tintable show
    private bool _applied;

    public AlertTintCoordinator(IAlertTintSink sink, Action<string> postToPage, Action<string>? trace = null)
    {
        _sink = sink;
        _postToPage = postToPage;
        _trace = trace;
    }

    /// <summary>Clears any tint, then starts a new tintable show and returns its sequence id for the "show" message.</summary>
    public int BeginShow()
    {
        lock (_gate)
        {
            ClearLocked();
            _activeSeq = ++_lastSeq;
            return _activeSeq;
        }
    }

    /// <summary>Deactivates the current show's id and removes the tint from the video.</summary>
    public void Clear()
    {
        lock (_gate) ClearLocked();
    }

    private void ClearLocked()
    {
        _activeSeq = 0;
        _applied = false;
        try { _sink.ClearTint(); }
        catch (Exception ex) { Trace(AlertLayerTrace.Error("tint-clear", ex)); }
    }

    /// <summary>Handles one page message; completes after the decode and the sink call. Never throws.</summary>
    public async Task HandleMessageAsync(string json)
    {
        try
        {
            if (json is null || json.Length > MaxMessageChars)
            {
                Trace($"alert-layer tint rejected: message too large ({json?.Length ?? 0} chars)");
                return;
            }

            if (!TryParse(json, out var seq, out var kind, out var width, out var height, out var png, out var reason))
            {
                Trace($"alert-layer tint rejected: {reason}");
                return;
            }

            lock (_gate)
            {
                if (seq != _activeSeq || _applied) return; // stale, cleared, or a duplicate
            }

            // The PNG decode is the only costly step: keep it off the UI thread.
            var decoded = await Task.Run(() =>
                AlertMaskDecoder.TryDecodeAlpha(png, width, height, out var alpha) ? alpha : null).ConfigureAwait(false);
            if (decoded is null)
            {
                Trace("alert-layer tint rejected: mask is not a valid PNG of the declared size");
                return;
            }

            var color = kind == "failed" ? AlertTintColors.Failed : AlertTintColors.Warning;
            lock (_gate)
            {
                if (seq != _activeSeq || _applied) return; // cleared or superseded while decoding
                _sink.SetTint(decoded, width, height, color.R, color.G, color.B);
                _applied = true;
                _postToPage(AlertLayerMessages.TintReady(seq));
            }

            Trace($"alert-layer tint applied kind={kind} size={width}x{height}");
        }
        catch (Exception ex)
        {
            Trace(AlertLayerTrace.Error("tint", ex));
        }
    }

    private static bool TryParse(
        string json, out int seq, out string kind, out int width, out int height, out string png, out string reason)
    {
        seq = width = height = 0;
        kind = png = "";
        reason = "";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() != "mask")
            {
                reason = "not a mask message";
                return false;
            }

            if (!TryGetInt(root, "seq", out seq) || !TryGetInt(root, "width", out width)
                || !TryGetInt(root, "height", out height))
            {
                reason = "missing seq/width/height";
                return false;
            }

            if (width <= 0 || height <= 0 || (long)width * height > AlertMaskDecoder.MaxPixels)
            {
                reason = $"bad mask size {width}x{height}";
                return false;
            }

            if (!root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String
                || kindElement.GetString() is not ("failed" or "warning"))
            {
                reason = "unknown kind";
                return false;
            }

            kind = kindElement.GetString()!;
            if (!root.TryGetProperty("png", out var pngElement) || pngElement.ValueKind != JsonValueKind.String)
            {
                reason = "missing png";
                return false;
            }

            png = pngElement.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            reason = "not json";
            return false;
        }
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private void Trace(string line)
    {
        try { _trace?.Invoke(line); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
