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
/// The session gate PLUS the raised-desktop (24H2+) layout (see <see
/// cref="DesktopGate.RaisedLayoutSkipReason()"/>).
/// </summary>
/// <remarks>
/// F4 (video-wallpaper-review-followups, <c>R3-realattach-assumes-raised-layout</c>): a fact whose
/// own assertions assume <c>SHELLDLL_DefView</c> sits directly under the video wallpaper host's
/// resolved parent must SKIP on the legacy WorkerW layout instead of reporting a failure that is
/// not actually a defect there -- xunit 2 cannot skip from inside a running test body (see the
/// <see cref="MeasuresAdmissionFactAttribute"/> remarks below for the same point made about a
/// second opt-in), so the runtime-observed machine characteristic has to be decided here, in the
/// attribute constructor, exactly like every other gate in this file.
/// </remarks>
internal sealed class RequiresRaisedDesktopLayoutFactAttribute : FactAttribute
{
    public RequiresRaisedDesktopLayoutFactAttribute()
    {
        if (DesktopGate.RaisedLayoutSkipReason() is { } reason)
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

/// <summary>
/// The session gate PLUS this diagnostic's own opt-in, because it CLOSES the user's Settings window
/// to force a cold launch.
/// </summary>
/// <remarks>
/// An attribute rather than an <c>if</c> in the body. xunit 2 cannot skip from inside a test, so the
/// body check could only write "NOT RUN" and return -- reporting PASSED for a diagnostic that did
/// nothing. A skip says it did not run; a green says the opposite of the truth.
/// </remarks>
internal sealed class MeasuresAdmissionFactAttribute : FactAttribute
{
    /// <summary>This diagnostic's own opt-in, on top of the shared desktop one.</summary>
    public const string Variable = "COSMICWIN_MEASURE_ADMISSION";

    public MeasuresAdmissionFactAttribute()
    {
        Skip = DesktopGate.AlsoRequires(
            DesktopGate.SessionSkipReason(),
            Environment.GetEnvironmentVariable(Variable),
            $"Set {Variable}=1 -- this CLOSES the Settings window to measure a cold launch.",
            "1");
    }
}

/// <summary>
/// The opt-in PLUS an explicit direction, because this spike PAINTS a DWM attribute onto every
/// trackable window cross-process and that outlives the run, the testhost and a reboot.
/// </summary>
/// <remarks>
/// Deliberately not the session gate: this reads and paints, it spawns nothing to be tiled. The
/// direction is demanded rather than defaulted -- five solution-wide runs once painted every border
/// red and the maintainer reported it as a CosmicWin defect. It was this spike.
/// </remarks>
internal sealed class PaintsWindowBordersFactAttribute : FactAttribute
{
    /// <summary>Must say WHICH direction; knowing what the variable touches is not enough.</summary>
    public const string Variable = "COSMICWIN_SPIKE_BORDER";

    public PaintsWindowBordersFactAttribute()
    {
        Skip = DesktopGate.AlsoRequires(
            DesktopGate.OptInSkipReason(),
            Environment.GetEnvironmentVariable(Variable),
            $"Set {Variable}=paint to repaint every window's border on this desktop, or =restore to undo it.",
            "paint",
            "restore");
    }
}
