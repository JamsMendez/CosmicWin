namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The gate that decides whether a desktop fact runs, pinned as a pure decision rather than felt
/// through the environment.
/// </summary>
/// <remarks>
/// <para>
/// The gate used to live inside the attribute constructors, reading environment variables and
/// process lists directly, which made it untestable: proving it would mean mutating process-wide
/// state that every other test class shares. Split out, it takes its inputs as arguments, so these
/// facts are headless, deterministic, and carry no <c>RequiresDesktop</c> trait -- they exercise the
/// DECISION, never a desktop.
/// </para>
/// <para>
/// Four gates, because four different things get demanded. The opt-in alone is the floor. The
/// session gate adds "no live window manager" for facts that spawn windows. The terminal gate adds a
/// binary that is not on every machine. The elevated gate adds Administrator. They compose from one
/// opt-in check instead of repeating it, which is what four separate copies across two assemblies
/// used to do.
/// </para>
/// <para>
/// The expensive probes arrive as delegates so they can be proven NOT to run. That is the whole
/// point of <see cref="TheWindowManagerIsNotProbedUntilTheOptInPasses"/>: enumerating processes
/// before checking a variable costs every `dotnet test` run something it never needed to spend.
/// </para>
/// </remarks>
public sealed class DesktopGateTests
{
    private const string TerminalPath = @"C:\tools\alacritty.exe";

    private static Func<bool> Never => () => false;

    private static Func<bool> Always => () => true;

    /// <summary>A probe that records whether anybody actually asked it.</summary>
    private sealed class CountingProbe
    {
        private readonly bool _answer;

        public CountingProbe(bool answer) => _answer = answer;

        public int Calls { get; private set; }

        public bool Read()
        {
            Calls++;
            return _answer;
        }
    }

    /// <summary>
    /// The default has to be OFF. These facts read and move whatever the machine happens to have
    /// open, so running them unasked on a developer's desktop is the failure mode, not a courtesy.
    /// </summary>
    [Fact]
    public void WithoutTheOptIn_EveryGateSkipsAndNamesTheVariable()
    {
        foreach (var reason in new[]
                 {
                     DesktopGate.OptInSkipReason(runFlag: null),
                     DesktopGate.SessionSkipReason(runFlag: null, Never),
                     DesktopGate.TerminalSkipReason(runFlag: null, Never, TerminalPath),
                     DesktopGate.ElevatedSkipReason(runFlag: null, Never),
                 })
        {
            Assert.NotNull(reason);
            Assert.Contains(DesktopGate.RunFlagVariable, reason);
        }
    }

    /// <summary>Anything other than the exact opt-in value is not an opt-in.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]
    public void WithSomethingOtherThanTheExactOptIn_TheGateStillSkips(string runFlag)
    {
        Assert.NotNull(DesktopGate.OptInSkipReason(runFlag));
        Assert.NotNull(DesktopGate.SessionSkipReason(runFlag, Never));
    }

    [Fact]
    public void WithTheOptInAndNothingElseDemanded_TheOptInGateRuns()
    {
        Assert.Null(DesktopGate.OptInSkipReason("1"));
    }

    [Fact]
    public void WithTheOptInAndNothingInTheWay_TheSessionGateRuns()
    {
        Assert.Null(DesktopGate.SessionSkipReason("1", Never));
    }

    /// <summary>
    /// A live CosmicWin TILES and ACTIVATES the very windows these facts spawn, so they fail on
    /// geometry that was never theirs. That misleading red cost several rounds of diagnosis before
    /// it was recognised, so the gate names it instead of letting it be suffered again.
    /// </summary>
    [Fact]
    public void WithTheWindowManagerRunning_TheSessionGateSkipsAndSaysHowToStopIt()
    {
        var reason = DesktopGate.SessionSkipReason("1", Always);

        Assert.NotNull(reason);
        Assert.Contains("tray", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerating every process on the machine to answer a question the opt-in already settled is
    /// work no run should pay for. The probe is a delegate precisely so this can be PROVEN rather
    /// than asserted in a comment.
    /// </summary>
    [Fact]
    public void TheWindowManagerIsNotProbedUntilTheOptInPasses()
    {
        var probe = new CountingProbe(answer: true);

        Assert.NotNull(DesktopGate.SessionSkipReason(runFlag: null, probe.Read));
        Assert.Equal(0, probe.Calls);

        Assert.NotNull(DesktopGate.SessionSkipReason("1", probe.Read));
        Assert.Equal(1, probe.Calls);
    }

    /// <summary>The same, for the two gates that layer something else on top of the opt-in.</summary>
    [Fact]
    public void NeitherTheTerminalNorTheElevatedGateProbesBeforeTheOptInPasses()
    {
        var windowManager = new CountingProbe(answer: true);
        var elevated = new CountingProbe(answer: false);

        Assert.NotNull(DesktopGate.TerminalSkipReason(runFlag: null, windowManager.Read, TerminalPath));
        Assert.NotNull(DesktopGate.ElevatedSkipReason(runFlag: null, elevated.Read));

        Assert.Equal(0, windowManager.Calls);
        Assert.Equal(0, elevated.Calls);
    }

    /// <summary>
    /// A missing terminal also settles it before the window manager is worth asking about.
    /// </summary>
    [Fact]
    public void WithNoTerminal_TheTerminalGateSkipsWithoutProbingTheWindowManager()
    {
        var windowManager = new CountingProbe(answer: false);

        var reason = DesktopGate.TerminalSkipReason("1", windowManager.Read, terminalPath: " ");

        Assert.NotNull(reason);
        Assert.Contains(SpawnedAlacrittyWindow.ExecutablePathEnvVar, reason);
        Assert.Equal(0, windowManager.Calls);
    }

    /// <summary>
    /// Precedence is not cosmetic: the opt-in is the thing the reader can act on first, and a
    /// message about a missing terminal on a machine that never asked to run desktop facts at all
    /// sends them looking for the wrong problem.
    /// </summary>
    [Fact]
    public void WithoutTheOptIn_TheTerminalGateReportsTheOptInRatherThanTheMissingTerminal()
    {
        var reason = DesktopGate.TerminalSkipReason(runFlag: null, Never, terminalPath: null);

        Assert.NotNull(reason);
        Assert.Contains(DesktopGate.RunFlagVariable, reason);
    }

    /// <summary>
    /// The terminal is the one requirement the nine formerly-ungated facts do NOT share: they spawn
    /// Notepad or only read monitors. Hanging it on them would make them skip on machines where they
    /// should run, which is why the session gate exists separately.
    /// </summary>
    [Fact]
    public void WithTheOptInButNoTerminal_OnlyTheTerminalGateSkips()
    {
        Assert.Null(DesktopGate.SessionSkipReason("1", Never));
        Assert.NotNull(DesktopGate.TerminalSkipReason("1", Never, terminalPath: " "));
    }

    [Fact]
    public void WithEverythingPresent_TheTerminalGateRuns()
    {
        Assert.Null(DesktopGate.TerminalSkipReason("1", Never, TerminalPath));
    }

    [Fact]
    public void WithTheWindowManagerRunning_TheTerminalGateSkipsEvenWithATerminal()
    {
        Assert.NotNull(DesktopGate.TerminalSkipReason("1", Always, TerminalPath));
    }

    /// <summary>
    /// Elevation layers on the opt-in and NOTHING else. A schtasks round-trip spawns no window, so
    /// demanding an idle window manager of it would be a requirement invented out of symmetry.
    /// </summary>
    [Fact]
    public void WithoutElevation_TheElevatedGateSkipsAndSaysWhyItIsNeeded()
    {
        var reason = DesktopGate.ElevatedSkipReason("1", Never);

        Assert.NotNull(reason);
        Assert.Contains("elevated", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithElevation_TheElevatedGateRuns()
    {
        Assert.Null(DesktopGate.ElevatedSkipReason("1", Always));
    }
}
