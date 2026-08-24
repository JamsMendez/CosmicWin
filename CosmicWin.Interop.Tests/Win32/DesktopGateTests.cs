namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The gate that decides whether a desktop fact runs, pinned as a pure decision rather than felt
/// through the environment.
/// </summary>
/// <remarks>
/// <para>
/// The gate used to live inside the attribute constructors, reading environment variables and
/// process lists directly, which made it untestable: proving it would mean mutating
/// process-wide state that every other test class shares. Split out, it takes its inputs as
/// arguments, so these facts are headless, deterministic, and carry no <c>RequiresDesktop</c>
/// trait -- they exercise the DECISION, never a desktop.
/// </para>
/// <para>
/// Two gates, because two different things are being demanded. The session gate is what any fact
/// touching the real desktop needs: an interactive session the maintainer opted into, and no live
/// window manager to tile the windows out from under it. The terminal gate adds a spawned binary
/// that is not on every machine. Nine facts were failing to ask for even the first one.
/// </para>
/// </remarks>
public sealed class DesktopGateTests
{
    private const string TerminalPath = @"C:\tools\alacritty.exe";

    /// <summary>
    /// The default has to be OFF. These facts read and move whatever the machine happens to have
    /// open, so running them unasked on a developer's desktop is the failure mode, not a courtesy.
    /// </summary>
    [Fact]
    public void WithoutTheOptIn_TheSessionGateSkipsAndNamesTheVariable()
    {
        var reason = DesktopGate.SessionSkipReason(runFlag: null, windowManagerRunning: false);

        Assert.NotNull(reason);
        Assert.Contains(DesktopGate.RunFlagVariable, reason);
    }

    /// <summary>Anything other than the exact opt-in value is not an opt-in.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]
    public void WithSomethingOtherThanTheExactOptIn_TheSessionGateStillSkips(string runFlag)
    {
        Assert.NotNull(DesktopGate.SessionSkipReason(runFlag, windowManagerRunning: false));
    }

    [Fact]
    public void WithTheOptInAndNothingInTheWay_TheSessionGateRuns()
    {
        Assert.Null(DesktopGate.SessionSkipReason(runFlag: "1", windowManagerRunning: false));
    }

    /// <summary>
    /// A live CosmicWin TILES and ACTIVATES the very windows these facts spawn, so they fail on
    /// geometry that was never theirs. That misleading red cost several rounds of diagnosis before
    /// it was recognised, so the gate names it instead of letting it be suffered again.
    /// </summary>
    [Fact]
    public void WithTheWindowManagerRunning_TheSessionGateSkipsAndSaysHowToStopIt()
    {
        var reason = DesktopGate.SessionSkipReason(runFlag: "1", windowManagerRunning: true);

        Assert.NotNull(reason);
        Assert.Contains("tray", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Precedence is not cosmetic: the opt-in is the thing the reader can act on first, and a
    /// message about a missing terminal on a machine that never asked to run desktop facts at all
    /// sends them looking for the wrong problem.
    /// </summary>
    [Fact]
    public void WithoutTheOptIn_TheTerminalGateReportsTheOptInRatherThanTheMissingTerminal()
    {
        var reason = DesktopGate.TerminalSkipReason(runFlag: null, windowManagerRunning: false, terminalPath: null);

        Assert.NotNull(reason);
        Assert.Contains(DesktopGate.RunFlagVariable, reason);
    }

    /// <summary>
    /// The terminal is the one requirement the nine ungated facts do NOT share: they spawn Notepad
    /// or only read monitors. Hanging this on them would make them skip on machines where they
    /// should run, which is why the session gate exists separately.
    /// </summary>
    [Fact]
    public void WithTheOptInButNoTerminal_OnlyTheTerminalGateSkips()
    {
        Assert.Null(DesktopGate.SessionSkipReason(runFlag: "1", windowManagerRunning: false));

        var reason = DesktopGate.TerminalSkipReason(runFlag: "1", windowManagerRunning: false, terminalPath: " ");

        Assert.NotNull(reason);
        Assert.Contains(SpawnedAlacrittyWindow.ExecutablePathEnvVar, reason);
    }

    [Fact]
    public void WithEverythingPresent_TheTerminalGateRuns()
    {
        Assert.Null(DesktopGate.TerminalSkipReason(
            runFlag: "1", windowManagerRunning: false, terminalPath: TerminalPath));
    }

    [Fact]
    public void WithTheWindowManagerRunning_TheTerminalGateSkipsEvenWithATerminal()
    {
        Assert.NotNull(DesktopGate.TerminalSkipReason(
            runFlag: "1", windowManagerRunning: true, terminalPath: TerminalPath));
    }
}
