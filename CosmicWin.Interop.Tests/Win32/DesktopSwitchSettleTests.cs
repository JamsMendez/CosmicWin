using CosmicWin.Interop.Win32.VirtualDesktops;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// A switch the shell has not finished performing yet is not a switch that failed.
/// </summary>
/// <remarks>
/// <para>
/// Measured on real hardware, from the report "Alt+number sometimes does nothing; I have to press
/// again". The trace caught it exactly:
/// </para>
/// <code>
/// 04:32:00.8101  ArrivingWindow hwnd=0x4158E sentTo=1 ok=True
/// 04:32:00.8280  SwitchDesktop arg=2 ok=False count=3->3 index=1->1 error=(none)
/// </code>
/// <para>
/// Eighteen milliseconds earlier CosmicWin had put the view back on desktop 1 itself, as the second
/// half of the arriving-window redirect. The user's chord then asked for desktop 2, the shell began
/// the switch, and the verification re-read landed while it was still settling -- so it reported
/// the desktop the user was leaving and the switch was declared failed. Nothing retries, so nothing
/// happened, silently, with <c>error=(none)</c>. The same chord succeeded 2.3 seconds later.
/// </para>
/// <para>
/// The verification itself stays. <c>SwitchTo</c> returns <see langword="void"/>, so asking the
/// shell afterwards is the only honest way to know whether the user actually moved -- assuming
/// success has bitten this repository before. What it lacked was TIME: a desktop switch is
/// animated, and one immediate read of an asynchronous operation measures the shell's reaction
/// speed rather than its answer.
/// </para>
/// <para>
/// Bounded, never open-ended. A switch the shell genuinely refuses must still report failure, and
/// must do it without hanging the dispatcher that is waiting on this call.
/// </para>
/// </remarks>
public sealed class DesktopSwitchSettleTests
{
    /// <summary>
    /// A shell that accepts the switch but keeps reporting the OLD desktop for a while, which is
    /// what an animation looks like from the outside.
    /// </summary>
    private sealed class SettlingDesktops : INativeVirtualDesktops
    {
        private readonly List<Guid> _ids = [];
        private Guid _current;
        private Guid _pending;
        private int _lag;
        private readonly int _readsBeforeSettling;

        /// <param name="readsBeforeSettling">
        /// How many reads report the old desktop before the switch becomes visible.
        /// <see cref="int.MaxValue"/> is a shell that never performs it at all.
        /// </param>
        public SettlingDesktops(int count, int readsBeforeSettling)
        {
            for (var i = 0; i < count; i++)
            {
                _ids.Add(Guid.NewGuid());
            }

            _current = _ids[0];
            _readsBeforeSettling = readsBeforeSettling;
        }

        public bool IsAvailable => true;

        public string? LastError => null;

        public int SwitchCalls { get; private set; }

        public Guid IdAt(int oneBasedIndex) => _ids[oneBasedIndex - 1];

        public IReadOnlyList<Guid> GetDesktopIds() => _ids.ToArray();

        public Guid GetCurrentDesktopId()
        {
            if (_pending == Guid.Empty)
            {
                return _current;
            }

            if (_lag > 0)
            {
                _lag--;
                return _current;
            }

            _current = _pending;
            _pending = Guid.Empty;
            return _current;
        }

        public void CreateDesktop() => _ids.Add(Guid.NewGuid());

        public void SwitchTo(Guid desktopId)
        {
            SwitchCalls++;
            if (_readsBeforeSettling == int.MaxValue)
            {
                return;
            }

            _pending = desktopId;
            _lag = _readsBeforeSettling;
        }

        public bool MoveWindowTo(nint windowHandle, Guid desktopId) => true;

        public void CloseCurrentDesktop() { }
    }

    /// <summary>Records the waiting instead of performing it, so the facts cost no wall clock.</summary>
    private sealed class RecordingWait
    {
        public List<TimeSpan> Waits { get; } = [];

        public void Wait(TimeSpan interval) => Waits.Add(interval);
    }

    [Fact]
    public void ASwitchTheShellReportsImmediately_Succeeds()
    {
        var wait = new RecordingWait();
        var native = new SettlingDesktops(count: 3, readsBeforeSettling: 0);
        var service = new Win32VirtualDesktopService(native, wait.Wait);

        Assert.True(service.TrySwitchTo(2));
        Assert.Equal(native.IdAt(2), native.GetCurrentDesktopId());
        Assert.Equal(1, native.SwitchCalls);
        Assert.Empty(wait.Waits);
    }

    /// <summary>
    /// The reported defect. The shell is mid-animation when the verification runs, and the switch is
    /// real -- so it must be reported as the success it is.
    /// </summary>
    [Fact]
    public void ASwitchTheShellIsStillAnimating_SucceedsOnceItSettles()
    {
        var wait = new RecordingWait();
        var native = new SettlingDesktops(count: 3, readsBeforeSettling: 4);
        var service = new Win32VirtualDesktopService(native, wait.Wait);

        Assert.True(service.TrySwitchTo(2));
        Assert.Null(service.LastError);
        Assert.NotEmpty(wait.Waits);
    }

    /// <summary>
    /// Asked once, waited for after. Re-issuing the switch each time round would fight an animation
    /// already in flight, and on the shell that means two desktop changes for one chord.
    /// </summary>
    [Fact]
    public void ASwitchStillSettling_IsAskedOfTheShellExactlyOnce()
    {
        var wait = new RecordingWait();
        var native = new SettlingDesktops(count: 3, readsBeforeSettling: 4);
        var service = new Win32VirtualDesktopService(native, wait.Wait);

        service.TrySwitchTo(2);

        Assert.Equal(1, native.SwitchCalls);
    }

    /// <summary>
    /// A refusal is still a refusal. The budget is what keeps "wait for it" from becoming "hang the
    /// dispatcher", and this is the fact that pins it as bounded rather than patient.
    /// </summary>
    [Fact]
    public void ASwitchTheShellNeverPerforms_FailsWithinABoundedBudget()
    {
        var wait = new RecordingWait();
        var native = new SettlingDesktops(count: 3, readsBeforeSettling: int.MaxValue);
        var service = new Win32VirtualDesktopService(native, wait.Wait);

        Assert.False(service.TrySwitchTo(2));
        Assert.NotEmpty(wait.Waits);
        Assert.True(
            wait.Waits.Aggregate(TimeSpan.Zero, (total, next) => total + next) <= TimeSpan.FromMilliseconds(500),
            $"Settling budget {wait.Waits.Aggregate(TimeSpan.Zero, (t, n) => t + n)} is too long to block a chord on.");
    }

    /// <summary>Already there: the shell is asked for nothing and nothing is waited for.</summary>
    [Fact]
    public void ASwitchToTheDesktopAlreadyInView_AsksTheShellForNothing()
    {
        var wait = new RecordingWait();
        var native = new SettlingDesktops(count: 3, readsBeforeSettling: 0);
        var service = new Win32VirtualDesktopService(native, wait.Wait);

        Assert.True(service.TrySwitchTo(1));
        Assert.Equal(0, native.SwitchCalls);
        Assert.Empty(wait.Waits);
    }
}
