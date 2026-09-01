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

    /// <summary>
    /// A chord that fails over and over must LOOK different from one that failed once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured on real hardware: the desktop chords went dead for 3.6 and 5.5 seconds at a time,
    /// and the trace showed exactly ONE <c>unmatched chord: Alt+165 raw=[RALT]</c> line for each
    /// dead stretch. That is not because one chord was lost. The publisher only records when the
    /// text CHANGES, so an identical failure repeating three hundred times and one failing once
    /// produce the same single line -- the instrument cannot tell the two apart, and the difference
    /// between them is the whole diagnosis.
    /// </para>
    /// <para>
    /// Counted in the text rather than in a second field on purpose: the publisher already writes
    /// on change, so a count that rides in the string makes every repeat visible without the
    /// publisher learning anything new, and without a second value that can be read a beat out of
    /// step with the first from the reconciliation thread.
    /// </para>
    /// <para>
    /// The first occurrence carries no suffix, so a chord that really did fail once reads exactly
    /// as it did before this existed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSameUnmatchedChordRepeating_CountsTheRepeats()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RALT",
        };

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT]", processor.LastUnmatched);

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT] x2", processor.LastUnmatched);

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT] x3", processor.LastUnmatched);
    }

    /// <summary>
    /// A DIFFERENT failure starts its own count, or the number would say how long the user has been
    /// pressing keys rather than how often this one chord failed.
    /// </summary>
    [Fact]
    public void ADifferentUnmatchedChord_RestartsTheCount()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RALT",
        };

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT] x2", processor.LastUnmatched);

        processor.Process(KeyboardKey.Escape, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Escape raw=[RALT]", processor.LastUnmatched);

        processor.Process(KeyboardKey.Escape, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Escape raw=[RALT] x2", processor.LastUnmatched);
    }

    /// <summary>
    /// A matched chord between two identical failures does not merge them into one run: the count
    /// answers "how many times in a row did THIS fail", and a chord that worked in between means
    /// the run ended.
    /// </summary>
    [Fact]
    public void AMatchedChordBetweenTwoFailures_EndsTheRun()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default)
        {
            PhysicalModifiers = () => "RALT",
        };

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT] x2", processor.LastUnmatched);

        processor.Process(KeyboardKey.D1, true, ModifierKeys.Alt, Sink());

        processor.Process(Unbound, true, ModifierKeys.Alt, Sink());
        Assert.Equal("Alt+Tab raw=[RALT]", processor.LastUnmatched);
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
