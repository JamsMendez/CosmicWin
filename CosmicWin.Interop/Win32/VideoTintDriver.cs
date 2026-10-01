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

    private VideoTintRequest? _request;

    // Worker-thread only.
    private IVideoTintRenderer? _renderer;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public VideoTintDriver(TimeProvider timeProvider, Func<IVideoTintRenderer> rendererFactory, TimeSpan failureBackoff)
    {
        _timeProvider = timeProvider;
        _rendererFactory = rendererFactory;
        _failureBackoff = failureBackoff;
    }

    /// <summary>
    /// True while a tint is requested or GPU objects still need releasing; false means the player
    /// must take exactly its untinted path (nothing was ever created).
    /// </summary>
    public bool IsEngaged => Volatile.Read(ref _request) is not null || _renderer is not null;

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
            return false;
        }

        if (_timeProvider.GetUtcNow() < _retryAt)
        {
            return false;
        }

        try
        {
            _renderer ??= _rendererFactory();
            if (_renderer.Prepare(device, backBuffer, in desc, request))
            {
                target = _renderer.Intermediate;
                return true;
            }
        }
        catch
        {
            // Falls through to the failure path: the frame pump must never see this.
        }

        FailAndBackOff();
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
                    return true;
                case VideoTintRenderResult.RecreateTarget:
                    DisposeRenderer();
                    return false;
            }
        }
        catch
        {
            // Treated as a plain failure.
        }

        FailAndBackOff();
        return false;
    }

    /// <summary>Worker thread: the tinted frame transfer itself failed; fall back and back off.</summary>
    public void Abort() => FailAndBackOff();

    /// <summary>Worker thread: drops every GPU object but keeps the request (playback stopped, the alert may still be showing).</summary>
    public void ReleaseGpu() => DisposeRenderer();

    public void Dispose()
    {
        Volatile.Write(ref _request, null);
        DisposeRenderer();
    }

    private void FailAndBackOff()
    {
        DisposeRenderer();
        _retryAt = _timeProvider.GetUtcNow() + _failureBackoff;
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
