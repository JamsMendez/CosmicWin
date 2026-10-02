using Windows.Win32.Graphics.Direct3D11;

namespace CosmicWin.Interop.Win32;

internal enum VideoTintRenderResult
{
    Ok,

    /// <summary>The pass failed; tear it down and back off before trying again.</summary>
    Failed,

    /// <summary>D2DERR_RECREATE_TARGET: the device is lost; tear down and rebuild on the next tick.</summary>
    RecreateTarget,
}

/// <summary>The GPU half of the tint pass; faked in headless tests.</summary>
internal interface IVideoTintRenderer : IDisposable
{
    /// <summary>Texture the video frame must be transferred into (valid after a successful <see cref="Prepare"/>).</summary>
    ID3D11Texture2D? Intermediate { get; }

    /// <summary>
    /// Makes the renderer ready for this back buffer and request (cheap when nothing changed).
    /// Must release anything pinning a previous back buffer before binding a different one.
    /// </summary>
    bool Prepare(ID3D11Device device, ID3D11Texture2D backBuffer, in D3D11_TEXTURE2D_DESC desc, VideoTintRequest request);

    /// <summary>Composes <see cref="Intermediate"/> into the back buffer: plain copy, then the tinted, masked pass.</summary>
    VideoTintRenderResult Render();
}

/// <summary>
/// Decides, once per frame, whether the player takes the tinted path, and owns the renderer's
/// lifetime and failure policy. Kept out of the player's Tick body so the off path is one volatile
/// read (<see cref="IsEngaged"/>) and the policy is testable without a GPU.
/// </summary>
/// <remarks>
/// <see cref="Set"/>/<see cref="Clear"/> are callable from any thread (a reference swap, no lock).
/// <see cref="TryBegin"/>, <see cref="Complete"/> and <see cref="Dispose"/> belong to the worker thread
/// (D3D/D2D objects are created, used and released there). Nothing here ever throws out of
/// <see cref="TryBegin"/>/<see cref="Complete"/>.
/// </remarks>
internal sealed class VideoTintDriver : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<IVideoTintRenderer> _rendererFactory;
    private readonly TimeSpan _failureBackoff;
    private readonly Action<string>? _onDiagnostic;

    private VideoTintRequest? _request;

    // Worker-thread only.
    private IVideoTintRenderer? _renderer;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastRecreateAt;
    private bool _inBackoff; // a failure was reported and its retry line is still pending
    private bool _failedSinceSuccess; // "recovered" is owed on the next successful frame
    private VideoTintRequest? _inFlight; // the request TryBegin handed to the renderer for this frame
    private VideoTintRequest? _announced; // the request whose first rendered frame was already announced

    /// <param name="onDiagnostic">
    /// Optional trace sink (worker thread). Receives one <c>video-tint failed ...</c> line per back-off
    /// window, <c>video-tint retry</c> when the window ends and the pass is tried again, and
    /// <c>video-tint recovered</c> on the first good frame after a failure. Lines carry the HRESULT and
    /// exception TYPE only (never a message). A throwing sink is swallowed.
    /// </param>
    public VideoTintDriver(
        TimeProvider timeProvider, Func<IVideoTintRenderer> rendererFactory, TimeSpan failureBackoff, Action<string>? onDiagnostic = null)
    {
        _timeProvider = timeProvider;
        _rendererFactory = rendererFactory;
        _failureBackoff = failureBackoff;
        _onDiagnostic = onDiagnostic;
    }

    /// <summary>
    /// True while a tint is requested or GPU objects still need releasing; false means the player
    /// must take exactly its untinted path (nothing was ever created).
    /// </summary>
    public bool IsEngaged => Volatile.Read(ref _request) is not null || _renderer is not null;

    /// <summary>Raised on the worker thread when the current request's tint first reached the screen (and again after a loss).</summary>
    public event Action? Rendered;

    /// <summary>Raised on the worker thread when a tint that was rendering stopped (the pass failed and backed off).</summary>
    public event Action? Lost;

    public void Set(VideoTintRequest request) => Volatile.Write(ref _request, request);

    public void Clear() => Volatile.Write(ref _request, null);

    /// <summary>
    /// Worker thread, before the frame transfer. True: transfer the frame into <paramref name="target"/>
    /// then call <see cref="Complete"/> (a successful Prepare guarantees a non-null target for the real renderer). False: transfer straight into the back buffer as usual.
    /// </summary>
    public bool TryBegin(ID3D11Device device, ID3D11Texture2D backBuffer, in D3D11_TEXTURE2D_DESC desc, out ID3D11Texture2D? target)
    {
        target = null;
        VideoTintRequest? request = Volatile.Read(ref _request);
        if (request is null)
        {
            DisposeRenderer();
            _retryAt = DateTimeOffset.MinValue; // a later request starts with a clean slate
            _lastRecreateAt = null;
            _inBackoff = false;
            _failedSinceSuccess = false;
            _inFlight = null;
            _announced = null;
            return false;
        }

        if (_timeProvider.GetUtcNow() < _retryAt)
        {
            return false;
        }

        if (_inBackoff)
        {
            _inBackoff = false;
            Report("video-tint retry");
        }

        _inFlight = request;
        try
        {
            _renderer ??= _rendererFactory();
            if (_renderer.Prepare(device, backBuffer, in desc, request))
            {
                target = _renderer.Intermediate;
                return true;
            }
        }
        catch (Exception ex)
        {
            // Falls through to the failure path: the frame pump must never see this.
            FailAndBackOff(Describe(ex));
            return false;
        }

        FailAndBackOff("result=PrepareFalse");
        return false;
    }

    /// <summary>Worker thread, after the frame landed in the intermediate. False: re-transfer into the back buffer.</summary>
    public bool Complete()
    {
        IVideoTintRenderer? renderer = _renderer;
        if (renderer is null)
        {
            return false;
        }

        try
        {
            switch (renderer.Render())
            {
                case VideoTintRenderResult.Ok:
                    if (_failedSinceSuccess)
                    {
                        _failedSinceSuccess = false;
                        Report("video-tint recovered");
                    }

                    // Only the request still wanted counts: a Clear/Set that raced this frame must not
                    // announce a tint nobody asked for any more.
                    if (_inFlight is { } rendered && ReferenceEquals(rendered, Volatile.Read(ref _request))
                        && !ReferenceEquals(_announced, rendered))
                    {
                        _announced = rendered;
                        Raise(Rendered);
                    }

                    return true;
                case VideoTintRenderResult.RecreateTarget:
                    // A device loss gets ONE immediate rebuild; losing it again inside the back-off
                    // window means rebuilding every tick would just repeat the loss, so back off.
                    DateTimeOffset now = _timeProvider.GetUtcNow();
                    if (_lastRecreateAt is { } last && now - last < _failureBackoff)
                    {
                        FailAndBackOff("result=RecreateTarget");
                        return false;
                    }

                    _lastRecreateAt = now;
                    DisposeRenderer();
                    return false;
            }
        }
        catch (Exception ex)
        {
            // Treated as a plain failure.
            FailAndBackOff(Describe(ex));
            return false;
        }

        FailAndBackOff("result=Failed");
        return false;
    }

    /// <summary>Worker thread: the tinted frame transfer itself failed; fall back and back off.</summary>
    public void Abort(Exception? error = null) =>
        FailAndBackOff(error is null ? "result=TransferFailed" : Describe(error));

    /// <summary>Worker thread: drops every GPU object but keeps the request (playback stopped, the alert may still be showing).</summary>
    public void ReleaseGpu()
    {
        DisposeRenderer();
        _announced = null; // the GPU objects are gone; the next good frame announces again
    }

    public void Dispose()
    {
        Volatile.Write(ref _request, null);
        DisposeRenderer();
    }

    private void FailAndBackOff(string detail)
    {
        DisposeRenderer();
        _retryAt = _timeProvider.GetUtcNow() + _failureBackoff;
        _failedSinceSuccess = true;
        if (_announced is not null)
        {
            _announced = null; // the screen lost its tint: a later good frame announces again
            Raise(Lost);
        }

        if (!_inBackoff)
        {
            _inBackoff = true;
            Report($"video-tint failed {detail} backoff={_failureBackoff.TotalSeconds:0.##}s");
        }
    }

    private static string Describe(Exception ex) => $"hr=0x{ex.HResult:X8} type={ex.GetType().Name}";

    private static void Raise(Action? handlers)
    {
        try
        {
            handlers?.Invoke();
        }
        catch
        {
            // A broken subscriber must never reach the frame pump.
        }
    }

    private void Report(string line)
    {
        try
        {
            _onDiagnostic?.Invoke(line);
        }
        catch
        {
            // A broken trace sink must never reach the frame pump.
        }
    }

    private void DisposeRenderer()
    {
        IVideoTintRenderer? renderer = _renderer;
        _renderer = null;
        if (renderer is null)
        {
            return;
        }

        try
        {
            renderer.Dispose();
        }
        catch
        {
            // Best-effort release.
        }
    }
}
