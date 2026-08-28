using CosmicWin.Interop.Win32.VirtualDesktops;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// The positional policy â€” index arithmetic, range limits, create-on-demand â€” with no live shell.
/// The maintainer chose positional over named desktops: the shell appends new desktops
/// at the end and gives them no durable number, so "desktop 3" can only mean "the third one".
/// </summary>
public sealed class Win32VirtualDesktopServiceTests
{
    private sealed class FakeDesktops : INativeVirtualDesktops
    {
        private readonly List<Guid> _ids = [];

        public FakeDesktops(int initialCount = 1)
        {
            for (var i = 0; i < initialCount; i++)
            {
                _ids.Add(Guid.NewGuid());
            }

            Current = _ids.Count > 0 ? _ids[0] : Guid.Empty;
        }

        public bool IsAvailable { get; set; } = true;

        public string? LastError => null;

        /// <summary>Simulates a shell that refuses to add desktops.</summary>
        public bool RefuseCreate { get; set; }

        public Guid Current { get; private set; }

        public int CreateCalls { get; private set; }

        public int SwitchCalls { get; private set; }

        public List<(nint Handle, Guid Desktop)> Moved { get; } = [];

        public IReadOnlyList<Guid> GetDesktopIds() => _ids.ToArray();

        public Guid GetCurrentDesktopId() => Current;

        public void CreateDesktop()
        {
            CreateCalls++;
            if (!RefuseCreate)
            {
                _ids.Add(Guid.NewGuid());
            }
        }

        public void SwitchTo(Guid desktopId)
        {
            SwitchCalls++;
            Current = desktopId;
        }

        public bool MoveWindowTo(nint windowHandle, Guid desktopId)
        {
            Moved.Add((windowHandle, desktopId));
            return true;
        }
    }

    [Fact]
    public void TrySwitchTo_AnIndexBeyondTheEnd_CreatesUntilItExists_ThenLandsThere()
    {
        var native = new FakeDesktops(initialCount: 1);
        var service = new Win32VirtualDesktopService(native);

        Assert.True(service.TrySwitchTo(3));

        Assert.Equal(2, native.CreateCalls);
        Assert.Equal(3, service.Count);
        Assert.Equal(3, service.CurrentIndex);
    }

    [Fact]
    public void TrySwitchTo_TheDesktopAlreadyShowing_DoesNotSwitchAgain()
    {
        var native = new FakeDesktops(initialCount: 3);
        var service = new Win32VirtualDesktopService(native);

        Assert.True(service.TrySwitchTo(1));

        // Re-switching would cost the user a desktop-change animation for no change at all.
        Assert.Equal(0, native.SwitchCalls);
        Assert.Equal(1, service.CurrentIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Win32VirtualDesktopService.MaxIndex + 1)]
    public void TrySwitchTo_OutOfRange_ChangesNothing(int index)
    {
        var native = new FakeDesktops(initialCount: 1);
        var service = new Win32VirtualDesktopService(native);

        Assert.False(service.TrySwitchTo(index));
        Assert.Equal(0, native.CreateCalls);
        Assert.Equal(0, native.SwitchCalls);
    }

    /// <summary>
    /// The gate the whole design hangs on. An unrecognised Windows build must lose virtual desktops
    /// entirely rather than call through a vtable that may no longer mean what we think.
    /// </summary>
    [Fact]
    public void WhenTheBuildIsUnsupported_EveryOperationIsAnInertNoOp()
    {
        var native = new FakeDesktops(initialCount: 3) { IsAvailable = false };
        var service = new Win32VirtualDesktopService(native);

        Assert.False(service.IsSupported);
        Assert.Equal(0, service.Count);
        Assert.Equal(0, service.CurrentIndex);
        Assert.False(service.TrySwitchTo(2));
        Assert.False(service.TryMoveWindowTo(new IntPtr(1), 2));

        Assert.Equal(0, native.CreateCalls);
        Assert.Equal(0, native.SwitchCalls);
        Assert.Empty(native.Moved);
    }

    [Fact]
    public void TryMoveWindowTo_CreatesTheTargetIfNeeded_AndDoesNotFollowTheWindow()
    {
        var native = new FakeDesktops(initialCount: 1);
        var service = new Win32VirtualDesktopService(native);
        var before = native.Current;

        Assert.True(service.TryMoveWindowTo(new IntPtr(0x1234), 2));

        var moved = Assert.Single(native.Moved);
        Assert.Equal(new IntPtr(0x1234), moved.Handle);
        Assert.Equal(native.GetDesktopIds()[1], moved.Desktop);

        // Sending a window away and being dragged after it are separate intents.
        Assert.Equal(0, native.SwitchCalls);
        Assert.Equal(before, native.Current);
    }

    [Fact]
    public void TryMoveWindowTo_NoWindow_IsRejectedBeforeAnythingIsCreated()
    {
        var native = new FakeDesktops(initialCount: 1);
        var service = new Win32VirtualDesktopService(native);

        Assert.False(service.TryMoveWindowTo(0, 3));
        Assert.Equal(0, native.CreateCalls);
    }

    /// <summary>
    /// A shell that accepts the call but does not grow the set would otherwise be asked forever.
    /// One refusal is enough to conclude it will not comply.
    /// </summary>
    [Fact]
    public void TrySwitchTo_WhenTheShellRefusesToCreate_GivesUpAfterOneAttempt()
    {
        var native = new FakeDesktops(initialCount: 1) { RefuseCreate = true };
        var service = new Win32VirtualDesktopService(native);

        Assert.False(service.TrySwitchTo(4));

        Assert.Equal(1, native.CreateCalls);
        Assert.Equal(0, native.SwitchCalls);
    }
}
