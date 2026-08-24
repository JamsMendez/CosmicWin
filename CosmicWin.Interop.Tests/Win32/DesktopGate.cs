using System.Diagnostics;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Decides whether a fact that touches the real desktop may run, as a pure function of its inputs.
/// </summary>
/// <remarks>
/// <para>
/// Two gates, because two different things are being demanded.
/// <see cref="SessionSkipReason"/> is what ANY fact touching the real desktop needs: an interactive
/// session the maintainer opted into, and no live window manager to tile the windows out from under
/// it. <see cref="TerminalSkipReason"/> adds a spawned terminal binary that is not on every machine.
/// Keeping them apart is the point: nine facts spawn Notepad or only read monitors, so hanging the
/// terminal requirement on them would make them SKIP on machines where they should run -- which is
/// how a gate meant to protect a desktop quietly becomes a gate that deletes coverage.
/// </para>
/// <para>
/// It takes its inputs as arguments rather than reading the environment, so the decision can be
/// pinned by headless facts. Read inline, it could only be proven by mutating process-wide state
/// that every other test class in the assembly shares.
/// </para>
/// </remarks>
public static class DesktopGate
{
    /// <summary>The opt-in. Absent, every desktop fact skips -- the default has to be OFF.</summary>
    public const string RunFlagVariable = "COSMICWIN_RUN_DESKTOP_TESTS";

    /// <summary>Exact match, not truthiness: "0", "true" and " 1" are all somebody NOT opting in.</summary>
    private const string OptIn = "1";

    /// <summary>Reads the two live inputs so an attribute does not have to.</summary>
    public static string? SessionSkipReason() =>
        SessionSkipReason(Environment.GetEnvironmentVariable(RunFlagVariable), WindowManagerRunning());

    /// <inheritdoc cref="SessionSkipReason()"/>
    public static string? TerminalSkipReason() =>
        TerminalSkipReason(
            Environment.GetEnvironmentVariable(RunFlagVariable),
            WindowManagerRunning(),
            Environment.GetEnvironmentVariable(SpawnedAlacrittyWindow.ExecutablePathEnvVar));

    /// <summary>
    /// <see langword="null"/> to run, otherwise the reason the fact is skipped.
    /// </summary>
    public static string? SessionSkipReason(string? runFlag, bool windowManagerRunning)
    {
        if (runFlag != OptIn)
        {
            return $"Set {RunFlagVariable}=1 in an interactive desktop session.";
        }

        if (windowManagerRunning)
        {
            // A live window manager TILES and ACTIVATES the very windows these facts spawn, so they
            // fail on geometry that was never theirs. That misleading red cost several rounds of
            // diagnosis before being recognised, so it is named rather than suffered. An elevated
            // instance cannot be stopped from a test run: exit it from the tray.
            return "CosmicWin.App is running and would tile the windows this fact spawns. " +
                   "Exit it from the tray first.";
        }

        return null;
    }

    /// <summary>
    /// The session gate plus a spawned terminal. The opt-in is checked FIRST on purpose: telling a
    /// reader about a missing terminal on a machine that never asked to run desktop facts at all
    /// sends them after the wrong problem.
    /// </summary>
    public static string? TerminalSkipReason(string? runFlag, bool windowManagerRunning, string? terminalPath)
    {
        if (runFlag != OptIn)
        {
            return $"Set {RunFlagVariable}=1 in an interactive desktop session.";
        }

        if (string.IsNullOrWhiteSpace(terminalPath))
        {
            return $"Set {SpawnedAlacrittyWindow.ExecutablePathEnvVar} to the terminal executable path.";
        }

        return SessionSkipReason(runFlag, windowManagerRunning);
    }

    private static bool WindowManagerRunning() => Process.GetProcessesByName("CosmicWin.App").Length > 0;
}
