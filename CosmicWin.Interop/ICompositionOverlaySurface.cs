namespace CosmicWin.Interop;

/// <summary>
/// The DirectComposition seam a WebView2 composition controller needs from whatever window hosts it:
/// the host window handle, a way to know the visual tree is usable (and when it was rebuilt), and one
/// overlay visual to assign to <c>CoreWebView2CompositionController.RootVisualTarget</c>.
/// </summary>
/// <remarks>
/// Implemented by <see cref="Win32.Win32VideoWallpaperHost"/> (overlay above the video swapchain) and
/// <see cref="Win32.Win32MiniSceneWindow"/> (overlay in an otherwise empty tree), so both WebView2
/// layers reuse the same composition-controller path. The visual is typed <see cref="object"/> for
/// the same reason <c>RootVisualTarget</c> is: no DirectComposition type crosses this assembly.
/// </remarks>
public interface ICompositionOverlaySurface
{
    /// <summary>The window the composition target is bound to.</summary>
    nint Hwnd { get; }

    /// <summary>Whether <see cref="AddCompositionOverlayVisual"/> can succeed right now.</summary>
    bool IsCompositionReady { get; }

    /// <summary>Bumped whenever the visual tree is rebuilt; an older overlay visual is then gone.</summary>
    int CompositionGeneration { get; }

    /// <summary>Adds the single overlay visual and commits; <see langword="null"/> when not ready.</summary>
    object? AddCompositionOverlayVisual();

    /// <summary>Removes the overlay visual, if any. Idempotent and never throws.</summary>
    void RemoveCompositionOverlayVisual();

    /// <summary>Commits pending composition changes. Never throws.</summary>
    void CommitComposition();
}

/// <summary>
/// A composition surface that is also a small always-on-top window the caller can move. Backed by
/// <see cref="Win32.Win32MiniSceneWindow"/>; tests substitute an in-memory fake.
/// </summary>
public interface IMiniSceneSurface : ICompositionOverlaySurface, IDisposable
{
    /// <summary>Creates the window and its composition tree at <paramref name="bounds"/>. False on failure.</summary>
    bool TryCreate(Rectangle bounds);

    /// <summary>Moves/resizes the window in real pixels, topmost and without activation.</summary>
    bool Place(Rectangle bounds);
}
