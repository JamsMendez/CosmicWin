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
