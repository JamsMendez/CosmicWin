using Windows.Win32;

namespace CosmicWin.App;

/// <summary>
/// Resolves the current OS foreground window handle. Owned by the App layer, unlike <see
/// cref="Interop.IWindow.TryActivate"/> (which Interop owns per design) — this is a single
/// branchless native read with no window-tracking state, matching the precedent of App's own
/// direct CsWin32 usage for the keyboard hook (<c>WindowsKeyboardHookPlatform</c>).
/// </summary>
public interface IForegroundWindowSource
{
    /// <summary>The current foreground window handle, or <see cref="IntPtr.Zero"/> if none.</summary>
    nint GetForegroundHandle();
}

internal sealed class Win32ForegroundWindowSource : IForegroundWindowSource
{
    public nint GetForegroundHandle() => PInvoke.GetForegroundWindow();
}
