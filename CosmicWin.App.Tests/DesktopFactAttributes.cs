using CosmicWin.Interop.Tests.Win32;

namespace CosmicWin.App.Tests;

/// <summary>
/// The opt-in and nothing more: an interactive desktop session the maintainer asked for.
/// </summary>
/// <remarks>
/// The keyboard-hook facts install a real low-level hook and type into a real window, but they
/// spawn nothing CosmicWin would tile, so this deliberately does NOT ask whether the window manager
/// is running. Whether it SHOULD -- a live CosmicWin has its own low-level hook installed and could
/// plausibly interfere -- is a real question and a separate one; this attribute reproduces the gate
/// that was already there rather than smuggling a new requirement in under a refactor.
/// </remarks>
internal sealed class RequiresDesktopOptInFactAttribute : FactAttribute
{
    public RequiresDesktopOptInFactAttribute()
    {
        if (DesktopGate.OptInSkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The opt-in, plus no live window manager to tile the spawned windows out from under the fact.
/// </summary>
internal sealed class RequiresDesktopSessionFactAttribute : FactAttribute
{
    public RequiresDesktopSessionFactAttribute()
    {
        if (DesktopGate.SessionSkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The opt-in, plus a genuinely elevated process. Skips, never fakes elevation.
/// </summary>
/// <remarks>
/// No window-manager check: a <c>schtasks</c> round-trip opens no window, so demanding an idle
/// window manager of it would be a requirement invented out of symmetry rather than need.
/// </remarks>
internal sealed class RequiresElevatedFactAttribute : FactAttribute
{
    public RequiresElevatedFactAttribute()
    {
        if (DesktopGate.ElevatedSkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}
