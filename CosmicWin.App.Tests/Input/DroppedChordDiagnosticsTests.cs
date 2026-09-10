using System.Threading.Channels;
using CosmicWin.App.Input;

namespace CosmicWin.App.Tests.Input;

/// <summary>
/// A chord that MATCHED, but was silently discarded because the dispatcher channel was full.
/// </summary>
/// <remarks>
/// <para>
/// Seven days of trace evidence showed desktop/layout chords "go dead" for seconds at a time and
/// then work again on a second press, with NOTHING written to the trace for the whole dead stretch.
/// <c>ActionDispatcher</c>'s channel is bounded with <c>BoundedChannelFullMode.Wait</c>, which reads
/// as "a write against a full channel blocks until there is room" -- but
/// <see cref="KeyboardEventProcessor.Process"/> writes through
/// <see cref="ChannelWriter{T}.TryWrite"/>, the one member on that channel that never blocks: on a
/// full channel it returns <c>false</c> at once, and the action a matched chord produced vanishes.
/// </para>
/// <para>
/// <see cref="UnmatchedChordDiagnosticsTests"/> covers the other silent path -- a chord that matched
/// NOTHING in <see cref="ChordTable"/> -- and cannot see this one: <c>RecordUnmatched</c> only runs
/// when <c>TryMatch</c> returns <c>false</c>, and a dropped chord is one <c>TryMatch</c> already said
/// yes to.
/// </para>
/// </remarks>
public sealed class DroppedChordDiagnosticsTests
{
    /// <summary>A channel with room for one write, and nothing taken from it -- so a single write fills it.</summary>
    private static ChannelWriter<HotkeyAction> FullChannel()
    {
        var channel = Channel.CreateBounded<HotkeyAction>(1);
        Assert.True(channel.Writer.TryWrite(new HotkeyAction(HotkeyActionKind.FocusLeft)));
        return channel.Writer;
    }

    /// <summary>Alt+D1 is a real chord, so an unmatched one needs a key the table does not carry.</summary>
    private const KeyboardKey Unbound = KeyboardKey.Tab;

    /// <summary>The instrument hole itself, closed: a matched chord dropped by a full channel is now counted and described.</summary>
    [Fact]
    public void AMatchedChordWrittenToAFullChannel_IncrementsDroppedAndSetsLastDropped()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, FullChannel());

        Assert.Equal(1, processor.Dropped);
        Assert.Equal("Alt+L", processor.LastDropped);
    }

    /// <summary>The ordinary case -- room in the channel -- must not move the new counter at all.</summary>
    [Fact]
    public void AMatchedChordWrittenToAChannelWithRoom_DoesNotIncrementDropped()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default);
        var channel = Channel.CreateBounded<HotkeyAction>(1);

        processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, channel.Writer);

        Assert.Equal(0, processor.Dropped);
        Assert.Null(processor.LastDropped);
    }

    /// <summary>
    /// The semantic correction this change makes. A dropped chord is a failure the user is living
    /// through, wearing a different cause than an unmatched one -- resetting the unmatched run for it
    /// would under-report exactly the dead stretch this diagnosis exists to measure.
    /// </summary>
    /// <remarks>
    /// Before this change, a matched chord -- dropped or not -- unconditionally cleared
    /// <c>_repeating</c>/<c>_repeats</c>, so this same sequence would have reported the fourth line as
    /// <c>"Alt+Tab"</c> (a fresh run of one) instead of <c>"Alt+Tab x3"</c> (the run continuing).
    /// </remarks>
    [Fact]
    public void ADroppedChord_DoesNotResetTheUnmatchedRepeatRun()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        processor.Process(Unbound, true, ModifierKeys.Alt, Channel.CreateBounded<HotkeyAction>(1).Writer);
        processor.Process(Unbound, true, ModifierKeys.Alt, Channel.CreateBounded<HotkeyAction>(1).Writer);
        Assert.Equal("Alt+Tab x2", processor.LastUnmatched);

        var suppressed = processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, FullChannel());
        Assert.False(suppressed);
        Assert.Equal(1, processor.Dropped);

        processor.Process(Unbound, true, ModifierKeys.Alt, Channel.CreateBounded<HotkeyAction>(1).Writer);
        Assert.Equal("Alt+Tab x3", processor.LastUnmatched);
    }

    /// <summary>
    /// A dropped chord must not be suppressed: the key was never actually delivered anywhere, so the
    /// focused application still needs to receive it. Existing behaviour, and this change must not
    /// touch it.
    /// </summary>
    [Fact]
    public void ADroppedChord_ReturnsFalse_SoTheFocusedApplicationStillReceivesTheKey()
    {
        var processor = new KeyboardEventProcessor(ChordTable.Default);

        var suppressed = processor.Process(KeyboardKey.L, true, ModifierKeys.Alt, FullChannel());

        Assert.False(suppressed);
    }
}
