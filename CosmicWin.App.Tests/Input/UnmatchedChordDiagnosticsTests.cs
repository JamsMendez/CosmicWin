using System.Threading.Channels;
using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.Input;

/// <summary>
/// What actually arrived when a chord did nothing.
/// </summary>
/// <remarks>
/// <para>
/// "The right Alt sometimes does not switch desktops; the left Alt always does" survived two
/// measured hypotheses. AltGr was refuted -- every visible window reported layout 0x0409, and the
/// right Alt produced no phantom Ctrl. Synthetic input releasing the held modifier was refuted for
/// this chord -- switching and creating desktops both go through COM, and the only production
/// sender of synthetic input is the CLOSE-desktop path.
/// </para>
/// <para>
/// It survived them because the instrument had a hole exactly the size of the defect: an unmatched
/// key was recorded only when <c>modifiers != None</c>, so a chord that failed BECAUSE its modifier
/// was missing looked identical to a key nobody pressed. The one case worth seeing was the one case
/// refused.
/// </para>
/// <para>
/// The hole is closed with the PHYSICAL modifier state rather than by recording everything.
/// <c>unmatched chord</c> is written to the trace unconditionally -- it is not behind the
/// <c>trace-dialogs</c> marker the dialog paths use -- so recording every bare keystroke would make
/// this an always-on keylogger over the user's own typing. Nothing is recorded unless a modifier is
/// physically down, which no ordinary typing satisfies and every failing chord does.
/// </para>
/// <para>
/// The raw sides are the answer, not a decoration: computed <c>Alt</c> against raw <c>RMENU</c>
/// says the computation is right and the fault is downstream, computed <c>None</c> against raw
/// <c>RMENU</c> says the computation lost it, and computed <c>None</c> against empty raw says the
/// key state itself was gone before the hook ever ran.
/// </para>
/// </remarks>
public sealed class UnmatchedChordDiagnosticsTests
{
    private static ChannelWriter<HotkeyAction> Sink() =>
        Channel.CreateBounded<HotkeyAction>(1).Writer;

    /// <summary>Alt+D1 is a real chord, so an unmatched one needs a key the table does not carry.</summary>
    private const KeyboardKey Unbound = KeyboardKey.Tab;

    [Fact]
    public void AnUnmatchedChordWithModifiers_RecordsTheRawSidesBesideTheComputedOnes()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RMENU",
        };

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());

        Assert.Equal("Alt+Tab raw=[RMENU]", processor.LastUnmatched);
    }

    /// <summary>
    /// The hole, closed. A modifier held but not computed is the whole defect, and it used to leave
    /// no record at all.
    /// </summary>
    [Fact]
    public void AKeyWithNoComputedModifierButOnePhysicallyDown_IsRecorded()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RMENU",
        };

        processor.Process(KeyboardKey.D1, true, ModifierKeys.None, Sink());

        Assert.Equal("None+D1 raw=[RMENU]", processor.LastUnmatched);
    }

    /// <summary>
    /// The privacy floor, and it is not negotiable. This trace is written whether or not the
    /// diagnostic marker exists, so a record taken with nothing held would log the user's typing.
    /// </summary>
    [Fact]
    public void OrdinaryTypingWithNothingHeld_IsNeverRecorded()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => string.Empty,
        };

        foreach (var key in new[] { KeyboardKey.D1, KeyboardKey.O, KeyboardKey.Q, Unbound })
        {
            processor.Process(key, true, ModifierKeys.None, Sink());
        }

        Assert.Null(processor.LastUnmatched);
    }

    /// <summary>
    /// A key-up records nothing either. Only the press is a chord attempt, and doubling every line
    /// would halve how far back the log reaches.
    /// </summary>
    [Fact]
    public void AKeyUp_RecordsNothing()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RMENU",
        };

        processor.Process(Unbound, false, ModifierKeys.Alt, Sink());

        Assert.Null(processor.LastUnmatched);
    }

    /// <summary>
    /// With no snapshot wired -- every test written before this existed, and any host that does not
    /// supply one -- the old behaviour stands exactly: modifiers recorded, bare keys not.
    /// </summary>
    [Fact]
    public void WithNoSnapshotWired_TheOriginalBehaviourIsUnchanged()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        processor.Process(KeyboardKey.D1, true, ModifierKeys.None, Sink());
        Assert.Null(processor.LastUnmatched);

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab", processor.LastUnmatched);
    }

    /// <summary>A matched chord is not an unmatched one, however much is held.</summary>
    [Fact]
    public void AMatchedChord_RecordsNothing()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "LMENU",
        };

        processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, Sink());

        Assert.Null(processor.LastUnmatched);
    }
}
