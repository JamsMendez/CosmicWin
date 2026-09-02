using CosmicWin.Interop.Win32;
using CosmicWin.Interop.Win32.VirtualDesktops;
using Xunit.Abstractions;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Proves that "is this window on the desktop being looked at" can actually be ASKED about another
/// process's window on this machine, and that the answer changes when the window moves.
/// </summary>
/// <remarks>
/// <para>
/// This is the fact <see cref="Win32Workspace.Poll"/>'s dismissal test hangs on, and it needed
/// measuring rather than assuming. Everything the unit tests pin is downstream of the answer: they
/// prove what happens once the shell has said yes, no, or nothing, and say nothing at all about
/// which of the three a real HWND actually produces. That distinction has already cost this project
/// twice on this very interface -- <c>MoveWindowToDesktop</c> is documented and still refuses every
/// window the caller does not own, which is all of them for a window manager.
/// </para>
/// <para>
/// Reading is the half that works cross-process, and this is where that claim stops being a claim.
/// Self-cleaning: the window goes back to the desktop it started on, and the assertions run first.
/// </para>
/// </remarks>
[Collection(RealDesktopCollection.Name)]
public sealed class VirtualDesktopMembershipTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(900);

    [RequiresDesktopFact]
    public void AnotherProcessesWindow_IsPlacedOnTheCurrentDesktop_AndStopsBeingWhenItIsMovedAway()
    {
        Thread.Sleep(Settle);

        var desktops = new Win32VirtualDesktopService();
        if (!desktops.IsSupported)
        {
            Assert.Fail($"Unsupported build, so nothing here can be measured: {desktops.LastError}");
        }

        using var spawned = SpawnedAlacrittyWindow.Spawn();
        Thread.Sleep(Settle);

        var startedOn = desktops.CurrentIndex;
        var source = new Win32NativeWindowSource();

        Assert.True(
            Win32VirtualDesktopQueries.TryIsWindowOnCurrentDesktop(spawned.Handle, out var here, out var askError),
            $"The shell refused to place another process's window at all, which leaves the dismissal " +
            $"test with nothing to read: {askError}");

        output.WriteLine($"window 0x{spawned.Handle:X} on desktop {startedOn}: onCurrent={here}");
        Assert.True(here);

        // Through the production seam as well, because that is the shape Poll consumes: a refusal
        // and a "no" are the same bool down there, and only this type separates them.
        Assert.True(source.IsOnCurrentDesktop(spawned.Handle));

        var target = startedOn == 1 ? 2 : 1;
        try
        {
            Assert.True(
                desktops.TryMoveWindowTo(spawned.Handle, target),
                $"TryMoveWindowTo reported failure, so the other half cannot be measured: {desktops.LastError}");
            Thread.Sleep(Settle);

            Assert.True(
                Win32VirtualDesktopQueries.TryIsWindowOnCurrentDesktop(spawned.Handle, out var away, out var awayError),
                $"The shell stopped answering once the window was cloaked on another desktop, which is " +
                $"the exact case the dismissal test must not misread: {awayError}");

            output.WriteLine($"after move to desktop {target}: onCurrent={away}");

            // The whole discriminator, in one line: a window cloaked by the user walking away is
            // NOT on the current desktop, and that is what keeps its tile.
            Assert.False(away);
            Assert.False(source.IsOnCurrentDesktop(spawned.Handle));
        }
        finally
        {
            desktops.TryMoveWindowTo(spawned.Handle, startedOn);
            Thread.Sleep(Settle);
        }
    }
}
