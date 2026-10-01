using System.Runtime.InteropServices;
using CosmicWin.Interop.Win32.VirtualDesktops;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Explorer restarts kill the cached shell proxies. These pin the recovery: a disconnect-class
/// failure re-resolves the managers and retries that call ONCE -- and nothing else does.
/// Fakes implement the COM interfaces directly, so no live shell is involved.
/// </summary>
public sealed class Win32NativeVirtualDesktopsReconnectTests
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    [Theory]
    [InlineData(unchecked((int)0x800706BA))]
    [InlineData(unchecked((int)0x800706BE))]
    [InlineData(unchecked((int)0x80010108))]
    [InlineData(unchecked((int)0x80010012))]
    [InlineData(unchecked((int)0x80004018))]
    public void IsDisconnect_is_true_for_every_listed_hresult(int hresult) =>
        Assert.True(ShellDisconnect.IsDisconnect(hresult));

    [Theory]
    [InlineData(0)]
    [InlineData(E_ACCESSDENIED)]
    [InlineData(unchecked((int)0x80004002))]
    [InlineData(unchecked((int)0x80004005))]
    public void IsDisconnect_is_false_for_a_live_shells_answers(int hresult) =>
        Assert.False(ShellDisconnect.IsDisconnect(hresult));

    [Fact]
    public void GetDesktopIds_reconnects_after_a_disconnect_and_returns_the_fresh_answer()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable);

        var ids = harness.Native.GetDesktopIds();

        Assert.Equal(harness.Healthy.DesktopIds, ids);
        Assert.Equal(2, harness.Resolves);
    }

    [Fact]
    public void GetCurrentDesktopId_reconnects_after_a_disconnect()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.Disconnected);

        Assert.Equal(harness.Healthy.DesktopIds[0], harness.Native.GetCurrentDesktopId());
        Assert.Equal(2, harness.Resolves);
    }

    [Fact]
    public void SwitchTo_reconnects_retries_once_and_clears_the_error()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.CallFailed);
        var target = harness.Healthy.DesktopIds[1];

        harness.Native.SwitchTo(target);

        Assert.Equal(target, harness.Healthy.Switched);
        Assert.Null(harness.Native.LastError);
    }

    [Theory]
    [InlineData(unchecked((int)0x800706BA))]
    [InlineData(unchecked((int)0x80010108))]
    public void CreateDesktop_is_retried_when_the_call_provably_never_reached_explorer(int hresult)
    {
        var harness = Harness.Create(firstDead: true, deadHr: hresult);

        harness.Native.CreateDesktop();

        Assert.Equal(2, harness.Resolves);
        Assert.Equal(1, harness.Healthy.Created);
    }

    [Theory]
    [InlineData(unchecked((int)0x800706BE))]
    [InlineData(unchecked((int)0x80010012))]
    [InlineData(unchecked((int)0x80004018))]
    public void CreateDesktop_reconnects_but_is_not_retried_when_the_call_may_have_run(int hresult)
    {
        var harness = Harness.Create(firstDead: false, deadHr: hresult, firstCreateDeadHr: hresult);

        harness.Native.CreateDesktop();

        // Reconnected, so the NEXT call works; not retried, so a create that did run is not doubled.
        Assert.Equal(2, harness.Resolves);
        Assert.Equal(0, harness.Healthy.Created);
        Assert.Contains("not retried", harness.Native.LastError);
        Assert.Contains($"0x{hresult:X8}", harness.Native.LastError);
    }

    [Fact]
    public void After_an_unretried_create_the_next_call_uses_the_fresh_managers()
    {
        var harness = Harness.Create(firstDead: false, deadHr: ShellDisconnect.CallFailed, firstCreateDeadHr: ShellDisconnect.CallFailed);

        harness.Native.CreateDesktop();
        harness.Native.CreateDesktop();

        Assert.Equal(2, harness.Resolves);
        Assert.Equal(1, harness.Healthy.Created);
    }

    [Fact]
    public void A_not_retried_create_degrades_to_a_clear_failure_in_the_service()
    {
        var harness = Harness.Create(firstDead: false, deadHr: ShellDisconnect.CallFailed, firstCreateDeadHr: ShellDisconnect.CallFailed);
        var service = new Win32VirtualDesktopService(harness.Native, _ => { });

        // Two desktops exist; asking for the third makes the service create one.
        var switched = service.TrySwitchTo(3);

        Assert.False(switched);
        Assert.Equal(0, harness.Healthy.Created);
        Assert.Contains("CreateDesktop did not grow the set", service.LastError);
        Assert.Contains("not retried", service.LastError);
    }

    [Fact]
    public void MoveWindowTo_reconnects_when_the_disconnect_is_thrown()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable);
        var target = harness.Healthy.DesktopIds[1];

        var moved = harness.Native.MoveWindowTo(0x50A22, target);

        Assert.True(moved);
        Assert.Equal(target, harness.Healthy.MovedTo);
    }

    [Fact]
    public void MoveWindowTo_reconnects_when_the_disconnect_is_a_returned_hresult()
    {
        // GetViewForHwnd is PreserveSig: the dead shell answers with hr < 0, nothing is thrown.
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable, deadThrows: false);
        var target = harness.Healthy.DesktopIds[1];

        var moved = harness.Native.MoveWindowTo(0x50A22, target);

        Assert.True(moved);
        Assert.Equal(2, harness.Resolves);
        Assert.Equal(target, harness.Healthy.MovedTo);
    }

    [Fact]
    public void A_non_disconnect_failure_does_not_reconnect()
    {
        var harness = Harness.Create(firstDead: true, deadHr: E_ACCESSDENIED);

        var ids = harness.Native.GetDesktopIds();

        Assert.Empty(ids);
        Assert.Equal(1, harness.Resolves);
        Assert.Contains("0x80070005", harness.Native.LastError);
    }

    [Fact]
    public void A_returned_non_disconnect_hresult_does_not_reconnect()
    {
        var harness = Harness.Create(firstDead: true, deadHr: E_ACCESSDENIED, deadThrows: false);

        var moved = harness.Native.MoveWindowTo(0x50A22, Guid.NewGuid());

        Assert.False(moved);
        Assert.Equal(1, harness.Resolves);
    }

    [Fact]
    public void A_shell_that_is_still_dead_reconnects_once_and_reports_it()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable, secondDead: true);

        var ids = harness.Native.GetDesktopIds();

        Assert.Empty(ids);
        Assert.Equal(2, harness.Resolves);
        Assert.Contains("after reconnect", harness.Native.LastError);
        Assert.Contains("0x800706BA", harness.Native.LastError);
    }

    [Fact]
    public void A_reconnect_that_cannot_resolve_keeps_the_old_managers_and_reports_why()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable, secondResolves: false);

        var ids = harness.Native.GetDesktopIds();

        Assert.Empty(ids);
        Assert.Equal(2, harness.Resolves);
        Assert.Contains("reconnect failed", harness.Native.LastError);
        Assert.Contains("explorer not back", harness.Native.LastError);
    }

    [Fact]
    public void The_probe_runs_once_and_never_again_on_reconnect()
    {
        var harness = Harness.Create(firstDead: true, deadHr: ShellDisconnect.ServerUnavailable);

        _ = harness.Native.IsAvailable;
        _ = harness.Native.GetDesktopIds();
        _ = harness.Native.GetDesktopIds();

        Assert.Equal(1, harness.ProbeRuns);
    }

    [Fact]
    public void A_healthy_shell_never_reconnects()
    {
        var harness = Harness.Create(firstDead: false, deadHr: 0);

        _ = harness.Native.GetDesktopIds();
        _ = harness.Native.GetCurrentDesktopId();

        Assert.Equal(1, harness.Resolves);
    }


    private sealed class Harness
    {
        public Win32NativeVirtualDesktops Native { get; private set; } = null!;

        /// <summary>The shell that answers correctly (the second one after a restart).</summary>
        public FakeShell Healthy { get; private set; } = null!;

        public int Resolves { get; private set; }

        public int ProbeRuns { get; private set; }

        public static Harness Create(
            bool firstDead,
            int deadHr,
            bool deadThrows = true,
            bool secondDead = false,
            bool secondResolves = true,
            int? firstCreateDeadHr = null)
        {
            var first = new FakeShell { DeadHr = firstDead ? deadHr : null, DeadThrows = deadThrows, CreateDeadHr = firstCreateDeadHr };
            var second = new FakeShell { DeadHr = secondDead ? deadHr : null, DeadThrows = deadThrows };
            var harness = new Harness { Healthy = firstDead || firstCreateDeadHr is not null ? second : first };
            var shells = new Queue<FakeShell>([first, second]);

            harness.Native = new Win32NativeVirtualDesktops(
                () =>
                {
                    harness.ProbeRuns++;
                    return true;
                },
                (out string? error) =>
                {
                    harness.Resolves++;
                    error = null;
                    if (harness.Resolves == 2 && !secondResolves)
                    {
                        error = "explorer not back";
                        return null;
                    }

                    var shell = shells.Dequeue();
                    return new Win32NativeVirtualDesktops.ShellManagers(shell, null, shell);
                });

            _ = harness.Native.IsAvailable;
            return harness;
        }
    }

    /// <summary>
    /// One shell generation: the internal manager and the view collection, dead or alive. A dead one
    /// fails every call the way a proxy into a killed explorer.exe does -- by throwing, or (for the
    /// PreserveSig method) by returning the HRESULT.
    /// </summary>
    private sealed class FakeShell : IVirtualDesktopManagerInternal, IApplicationViewCollection
    {
        public int? DeadHr { get; init; }

        public bool DeadThrows { get; init; } = true;

        public IReadOnlyList<Guid> DesktopIds { get; } = [Guid.NewGuid(), Guid.NewGuid()];

        public Guid Switched { get; private set; }

        public Guid MovedTo { get; private set; }

        public int Created { get; private set; }

        /// <summary>Makes ONLY CreateDesktop fail, the way a call lost mid-flight does.</summary>
        public int? CreateDeadHr { get; init; }

        private void ThrowIfDead()
        {
            if (DeadHr is { } hr)
            {
                throw new COMException("dead", hr);
            }
        }

        public void GetDesktops(out IObjectArray desktops)
        {
            ThrowIfDead();
            desktops = new FakeArray(DesktopIds.Select(id => (object)new FakeDesktop(id)).ToArray());
        }

        public IVirtualDesktop GetCurrentDesktop()
        {
            ThrowIfDead();
            return new FakeDesktop(DesktopIds[0]);
        }

        public void SwitchDesktop(IVirtualDesktop desktop)
        {
            ThrowIfDead();
            Switched = desktop.GetId();
        }

        public IVirtualDesktop CreateDesktop()
        {
            ThrowIfDead();
            if (CreateDeadHr is { } hr)
            {
                throw new COMException("dead", hr);
            }

            Created++;
            return new FakeDesktop(Guid.NewGuid());
        }

        public void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop)
        {
            ThrowIfDead();
            MovedTo = desktop.GetId();
        }

        public int GetViewForHwnd(IntPtr hwnd, out IApplicationView view)
        {
            view = null!;
            if (DeadHr is { } hr)
            {
                return DeadThrows ? throw new COMException("dead", hr) : hr;
            }

            view = new FakeView();
            return 0;
        }

        public int GetCount() => throw new NotSupportedException();

        public int CanViewMoveDesktops(IntPtr view, out int can) => throw new NotSupportedException();

        public int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop) =>
            throw new NotSupportedException();

        public int SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop) => throw new NotSupportedException();

        public int MoveDesktop(IVirtualDesktop desktop, int index) => throw new NotSupportedException();

        public int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback) => throw new NotSupportedException();

        public int FindDesktop(ref Guid desktopId, out IVirtualDesktop desktop) => throw new NotSupportedException();

        public int GetViews(out IObjectArray array) => throw new NotSupportedException();

        public int GetViewsByZOrder(out IObjectArray array) => throw new NotSupportedException();

        public int GetViewsByAppUserModelId(IntPtr id, out IObjectArray array) => throw new NotSupportedException();
    }

    private sealed class FakeView : IApplicationView;

    private sealed class FakeDesktop(Guid id) : IVirtualDesktop
    {
        public Guid GetId() => id;

        public int IsViewVisible(IntPtr view, out int visible) => throw new NotSupportedException();

        public int GetName(out IntPtr name) => throw new NotSupportedException();

        public int GetWallpaperPath(out IntPtr path) => throw new NotSupportedException();

        public int IsRemote(out int remote) => throw new NotSupportedException();
    }

    private sealed class FakeArray(object[] items) : IObjectArray
    {
        public void GetCount(out int count) => count = items.Length;

        public void GetAt(int index, ref Guid interfaceId, out object instance) => instance = items[index];
    }
}
