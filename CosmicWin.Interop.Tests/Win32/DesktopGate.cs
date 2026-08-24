using System.Diagnostics;
using System.Security.Principal;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Decides whether a fact that touches the real desktop may run, as a pure function of its inputs.
/// </summary>
/// <remarks>
/// <para>
/// Four gates, because four different things get demanded, and they compose from ONE opt-in check
/// rather than repeating it. <see cref="OptInSkipReason(string?)"/> is the floor: an interactive
/// session the maintainer asked for. <see cref="SessionSkipReason(string?, Func{bool})"/> adds "no
/// live window manager" for facts that spawn windows.
/// <see cref="TerminalSkipReason(string?, Func{bool}, string?)"/> adds a terminal binary that is not
/// on every machine. <see cref="ElevatedSkipReason(string?, Func{bool})"/> adds Administrator.
/// </para>
/// <para>
/// Keeping them apart is the point. Nine facts spawn Notepad or only read monitors, so hanging the
/// terminal requirement on them would make them SKIP on machines where they should run -- that is
/// how a gate meant to protect a desktop quietly becomes a gate that deletes coverage. Equally, a
/// <c>schtasks</c> round-trip opens no window, so demanding an idle window manager of it would be a
/// requirement invented out of symmetry rather than need.
/// </para>
/// <para>
/// The expensive probes arrive as delegates so the opt-in can settle the question before they run.
/// Enumerating every process on the machine to answer something a missing environment variable
/// already decided is work every <c>dotnet test</c> paid for and never needed. The terminal path
/// stays a plain string on purpose: reading an environment variable is a dictionary lookup with no
/// cost worth deferring, and pretending otherwise would buy symmetry with a delegate nobody needs.
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

    /// <summary>Reads the live opt-in so an attribute does not have to.</summary>
    public static string? OptInSkipReason() =>
        OptInSkipReason(Environment.GetEnvironmentVariable(RunFlagVariable));

    /// <inheritdoc cref="OptInSkipReason()"/>
    public static string? SessionSkipReason() =>
        SessionSkipReason(Environment.GetEnvironmentVariable(RunFlagVariable), WindowManagerRunning);

    /// <inheritdoc cref="OptInSkipReason()"/>
    public static string? TerminalSkipReason() =>
        TerminalSkipReason(
            Environment.GetEnvironmentVariable(RunFlagVariable),
            WindowManagerRunning,
            Environment.GetEnvironmentVariable(SpawnedAlacrittyWindow.ExecutablePathEnvVar));

    /// <inheritdoc cref="OptInSkipReason()"/>
    public static string? ElevatedSkipReason() =>
        ElevatedSkipReason(Environment.GetEnvironmentVariable(RunFlagVariable), Elevated);

    /// <summary>
    /// <see langword="null"/> to run, otherwise the reason the fact is skipped. Every other gate
    /// starts here, so the opt-in message is the one a reader sees first on a machine that never
    /// asked for any of this.
    /// </summary>
    public static string? OptInSkipReason(string? runFlag) =>
        runFlag == OptIn ? null : $"Set {RunFlagVariable}=1 in an interactive desktop session.";

    /// <summary>The opt-in, plus no live window manager to tile the spawned windows away.</summary>
    public static string? SessionSkipReason(string? runFlag, Func<bool> windowManagerRunning)
    {
        if (OptInSkipReason(runFlag) is { } optIn)
        {
            return optIn;
        }

        // A live window manager TILES and ACTIVATES the very windows these facts spawn, so they
        // fail on geometry that was never theirs. That misleading red cost several rounds of
        // diagnosis before being recognised, so it is named rather than suffered. An elevated
        // instance cannot be stopped from a test run: exit it from the tray.
        return windowManagerRunning()
            ? "CosmicWin.App is running and would tile the windows this fact spawns. " +
              "Exit it from the tray first."
            : null;
    }

    /// <summary>
    /// The session gate plus a spawned terminal. The terminal is checked BEFORE the window manager
    /// so the precedence the attribute has always reported stays byte-identical.
    /// </summary>
    public static string? TerminalSkipReason(string? runFlag, Func<bool> windowManagerRunning, string? terminalPath)
    {
        if (OptInSkipReason(runFlag) is { } optIn)
        {
            return optIn;
        }

        return string.IsNullOrWhiteSpace(terminalPath)
            ? $"Set {SpawnedAlacrittyWindow.ExecutablePathEnvVar} to the terminal executable path."
            : SessionSkipReason(runFlag, windowManagerRunning);
    }

    /// <summary>
    /// The opt-in plus Administrator, and NOTHING else -- a <c>schtasks</c> round-trip spawns no
    /// window, so it has no business asking whether a window manager is running.
    /// </summary>
    public static string? ElevatedSkipReason(string? runFlag, Func<bool> elevated)
    {
        if (OptInSkipReason(runFlag) is { } optIn)
        {
            return optIn;
        }

        return elevated()
            ? null
            : "Requires an elevated (Administrator) process -- schtasks /RL HIGHEST needs elevation.";
    }

    private static bool WindowManagerRunning() => Process.GetProcessesByName("CosmicWin.App").Length > 0;

    private static bool Elevated() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
