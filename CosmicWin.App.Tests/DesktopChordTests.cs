using CosmicWin.App.Input;
using CosmicWin.Interop;

namespace CosmicWin.App.Tests;

/// <summary>
/// The <c>Alt+N</c> / <c>Alt+Shift+N</c> virtual-desktop chords, from the key the hook sees to the
/// service call it produces.
/// </summary>
public sealed class DesktopChordTests
{
    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        public bool IsSupported => true;

        public int Count => 1;

        public int CurrentIndex => 1;

        public string? LastError => null;

        public Guid CurrentDesktopId => Guid.Empty;

        public List<int> Switched { get; } = [];

        public List<(nint Handle, int Index)> Moved { get; } = [];

        public bool TrySwitchTo(int oneBasedIndex)
        {
            Switched.Add(oneBasedIndex);
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex)
        {
            Moved.Add((windowHandle, oneBasedIndex));
            return true;
        }
    }

    private sealed class FixedForeground(nint handle) : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => handle;
    }

    /// <summary>
    /// No desktop chord can carry an argument the service refuses on RANGE alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ActionExecutor</c> hands focus off BEFORE asking the shell to move a window, and only when
    /// the move is not already known to be impossible for free. That guard tests two things -- a
    /// supported build and a foreground window to send -- and skips the service's third free
    /// refusal, an index outside <c>1..MaxIndex</c>. Skipping it is correct only while no chord can
    /// produce such an index, and that is an invariant across two files rather than a property of
    /// either, so it is pinned here rather than asserted in a comment.
    /// </para>
    /// <para>
    /// Note what is NOT a free refusal: an index larger than the number of desktops that currently
    /// exist. The service CREATES desktops until the index exists, by design, so
    /// <c>Alt+Shift+5</c> on a two-desktop machine is an ordinary successful move and not a refusal
    /// at all.
    /// </para>
    /// <para>
    /// If a chord for a tenth desktop is ever added, this fails first and says why: the executor's
    /// guard has to grow with it or that chord pays two activations for a move that cannot happen.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryDesktopChord_CarriesAnArgumentTheServiceWillNotRefuseOnRange()
    {
        // The range Win32VirtualDesktopService.TryResolve accepts, restated on the side that has to
        // stay inside it. Interop's copy is internal, so this cannot reference the constant itself.
        const int maxIndex = 9;

        // The WHOLE key and modifier space the table can hold, not the declared members of
        // KeyboardKey. A tenth desktop chord would be registered as `D1 + 9`, a byte with no name in
        // that enum, so a sweep over Enum.GetValues would walk straight past the one registration
        // this fact exists to catch -- confirmed by mutation, it passed happily with the tenth
        // chord in place. The table is a flat 256 x 8 array and TryMatch takes any byte, so the
        // exhaustive sweep costs nothing and cannot be outflanked by an unnamed key.
        var found = 0;
        for (var key = 0; key <= byte.MaxValue; key++)
        {
            for (var modifiers = 0; modifiers < 8; modifiers++)
            {
                if (!ChordTable.Default.TryMatch((ModifierKeys)modifiers, (KeyboardKey)key, out var action)
                    || action.Kind is not (HotkeyActionKind.SwitchDesktop or HotkeyActionKind.MoveWindowToDesktop))
                {
                    continue;
                }

                Assert.InRange(action.Argument, 1, maxIndex);
                found++;
            }
        }

        // Exact, not a lower bound. A sweep that matched nothing would pass every assertion above
        // while asserting nothing at all, and an exact count also fails on a tenth chord a second
        // way -- belt and braces on the one fact that has to notice a chord nobody told it about.
        Assert.Equal(18, found);
    }

    [Theory]
    [InlineData(KeyboardKey.D1, 1)]
    [InlineData(KeyboardKey.D5, 5)]
    [InlineData(KeyboardKey.D9, 9)]
    public void AltDigit_MatchesSwitchDesktop_CarryingTheNumberAsTheArgument(KeyboardKey key, int expected)
    {
        Assert.True(ChordTable.Default.TryMatch(ModifierKeys.Alt, key, out var action));
        Assert.Equal(HotkeyActionKind.SwitchDesktop, action.Kind);
        Assert.Equal(expected, action.Argument);
    }

    [Theory]
    [InlineData(KeyboardKey.D1, 1)]
    [InlineData(KeyboardKey.D9, 9)]
    public void AltShiftDigit_MatchesMoveWindowToDesktop(KeyboardKey key, int expected)
    {
        Assert.True(ChordTable.Default.TryMatch(ModifierKeys.Shift | ModifierKeys.Alt, key, out var action));
        Assert.Equal(HotkeyActionKind.MoveWindowToDesktop, action.Kind);
        Assert.Equal(expected, action.Argument);
    }

    /// <summary>
    /// The digits must not have quietly taken over the resize row: Alt+Ctrl is a different chord and
    /// stays unbound for digits, so it passes through to whatever has focus.
    /// </summary>
    [Fact]
    public void AltCtrlDigit_IsNotBound()
    {
        Assert.False(ChordTable.Default.TryMatch(ModifierKeys.Control | ModifierKeys.Alt, KeyboardKey.D1, out _));
    }

    /// <summary>
    /// A desktop chord is answered even when the foreground window is one CosmicWin does not track.
    /// Switching desktops is about what the user is looking at, not about the tiling tree, and a
    /// tree-first dispatch would have made the chord dead on an empty or excluded desktop.
    /// </summary>
    [Fact]
    public async Task SwitchDesktop_WithAnUntrackedForegroundWindow_StillReachesTheService()
    {
        var desktops = new FakeVirtualDesktops();
        var executor = new ActionExecutor(
            new Layout.LayoutTree(), new WindowRegistry(), new FixedForeground(0x999))
        {
            VirtualDesktops = desktops,
        };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, 3), CancellationToken.None);

        Assert.Equal(3, Assert.Single(desktops.Switched));
    }

    [Fact]
    public async Task MoveWindowToDesktop_SendsTheREALForegroundHandle()
    {
        var desktops = new FakeVirtualDesktops();
        var executor = new ActionExecutor(
            new Layout.LayoutTree(), new WindowRegistry(), new FixedForeground(0xABC))
        {
            VirtualDesktops = desktops,
        };

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.MoveWindowToDesktop, 2), CancellationToken.None);

        var moved = Assert.Single(desktops.Moved);
        Assert.Equal(new IntPtr(0xABC), moved.Handle);
        Assert.Equal(2, moved.Index);
    }

    /// <summary>Nothing wired: the chord is consumed, and no exception reaches the dispatcher loop.</summary>
    [Fact]
    public async Task DesktopChord_WithNoServiceWired_IsAQuietNoOp()
    {
        var executor = new ActionExecutor(
            new Layout.LayoutTree(), new WindowRegistry(), new FixedForeground(0x1));

        await executor.ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, 2), CancellationToken.None);
    }
}
