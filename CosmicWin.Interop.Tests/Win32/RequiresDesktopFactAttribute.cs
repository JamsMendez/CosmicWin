namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Marks a fact that needs an interactive desktop session AND an external terminal binary (see
/// <see cref="SpawnedAlacrittyWindow.ExecutablePathEnvVar"/>). Mirrors the same gate
/// <c>CosmicWin.App.Tests</c>' desktop suite already uses, so both projects opt in through one
/// environment variable rather than two conventions. The <c>RequiresDesktop</c> trait alone is not
/// enough here: unlike the Notepad-based facts, these spawn a binary that is not present on every
/// machine, so they must SKIP rather than fail when it is missing.
/// </summary>
internal sealed class RequiresDesktopFactAttribute : FactAttribute
{
    public RequiresDesktopFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COSMICWIN_RUN_DESKTOP_TESTS") != "1")
        {
            Skip = "Set COSMICWIN_RUN_DESKTOP_TESTS=1 in an interactive desktop session.";
        }
        else if (string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(SpawnedAlacrittyWindow.ExecutablePathEnvVar)))
        {
            Skip = $"Set {SpawnedAlacrittyWindow.ExecutablePathEnvVar} to the terminal executable path.";
        }
        else if (System.Diagnostics.Process.GetProcessesByName("CosmicWin.App").Length > 0)
        {
            // A live window manager TILES and ACTIVATES the very windows these facts spawn, so they
            // fail on geometry that was never theirs. That misleading red cost several rounds of
            // diagnosis before being recognised, so it is named rather than suffered. An elevated
            // instance cannot be stopped from a test run: exit it from the tray.
            Skip = "CosmicWin.App is running and would tile the windows this fact spawns. Exit it from the tray first.";
        }
    }
}
