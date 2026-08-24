namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Marks a fact that needs an interactive desktop session the maintainer opted into, and no live
/// window manager to tile the windows out from under it.
/// </summary>
/// <remarks>
/// The <c>RequiresDesktop</c> trait is NOT this gate. The trait is what CI filters on; it does
/// nothing on a developer's machine, where a plain <c>dotnet test</c> still runs the fact against
/// whatever that desktop happens to have open. Nine facts carried the trait and no gate for exactly
/// that reason. The decision itself lives in <see cref="DesktopGate"/>, where it can be proven.
/// </remarks>
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
/// The session gate PLUS an external terminal binary (see
/// <see cref="SpawnedAlacrittyWindow.ExecutablePathEnvVar"/>).
/// </summary>
/// <remarks>
/// Deliberately a separate attribute rather than the only one. Unlike the Notepad-based facts,
/// these spawn a binary that is not present on every machine, so they must SKIP rather than fail
/// when it is missing -- but demanding it of a fact that only reads monitors would delete coverage
/// on machines where that fact runs perfectly well.
/// </remarks>
internal sealed class RequiresDesktopFactAttribute : FactAttribute
{
    public RequiresDesktopFactAttribute()
    {
        if (DesktopGate.TerminalSkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}
