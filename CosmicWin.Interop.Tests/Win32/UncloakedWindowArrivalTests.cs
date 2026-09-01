using CosmicWin.Interop;
using CosmicWin.Interop.Win32;

namespace CosmicWin.Interop.Tests.Win32;

/// <summary>
/// Returning to a virtual desktop is not nine windows being born.
/// </summary>
/// <remarks>
/// <para>
/// <c>WindowArrival</c> exists so the arriving-window redirect only ever overrules a window Windows
/// has just BORN on the wrong desktop. It was wired to <see cref="NativeWindowEventKind.Created"/>,
/// which at the time meant three different things -- a real create, a show, and an UNCLOAK. DWM
/// uncloaks every window on a virtual desktop the instant the user returns to it, so the redirect
/// still received them all as births and still dragged them away.
/// </para>
/// <para>
/// Measured after the first fix landed: CosmicWin started with two populated desktops and the
/// second emptied itself into the first. The trace named it exactly --
/// <c>ArrivingWindow hwnd=0x4158E sentTo=1 ok=True</c> followed by
/// <c>added ... redirected=True</c>, for windows that had been sitting there all along.
/// </para>
/// <para>
/// These facts sit at the workspace boundary rather than on the real hook, because that is where
/// the lie was told: the native source is what must stop calling an uncloak a birth, and this is
/// the seam where a consumer can see the difference without a live desktop.
/// </para>
/// </remarks>
public sealed class UncloakedWindowArrivalTests
{
    private static readonly Rectangle Somewhere = Rectangle.FromSize(0, 0, 800, 600);

    /// <summary>The whole defect: a window that was already there is not a new one.</summary>
    [Fact]
    public void AnUncloakedWindow_ArrivesAsAdopted()
    {
        var native = new FakeNativeWindowSource();
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        native.SimulateWindowUncloakedWithEvent(new IntPtr(7), "Alacritty", Somewhere);

        Assert.NotNull(received);
        Assert.Equal(WindowArrival.Adopted, received!.Arrival);
    }

    /// <summary>
    /// The guard. A window genuinely born must still arrive as one, or narrowing the redirect would
    /// undo the defect it was written for -- a window opening on a desktop the user is not on.
    /// </summary>
    [Fact]
    public void AGenuinelyCreatedWindow_StillArrivesAsCreated()
    {
        var native = new FakeNativeWindowSource();
        var workspace = new Win32Workspace(native);
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        native.SimulateWindowCreatedWithEvent(new IntPtr(8), "Calculator", Somewhere);

        Assert.NotNull(received);
        Assert.Equal(WindowArrival.Created, received!.Arrival);
    }

    /// <summary>
    /// The startup sweep adopts too. Every window standing when the manager starts was put there by
    /// the user, and none of them is a decision to overrule.
    /// </summary>
    [Fact]
    public void AWindowAlreadyStandingAtStartup_ArrivesAsAdopted()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(9), "Notepad", Somewhere);
        var workspace = new Win32Workspace(native);
        var arrivals = new List<WindowArrival>();
        workspace.WindowAdded += (_, e) => arrivals.Add(e.Arrival);

        workspace.Open();

        Assert.Equal([WindowArrival.Adopted], arrivals);
    }

    /// <summary>
    /// An uncloak for a window already tracked announces nothing. Returning to a desktop uncloaks
    /// everything on it, every time, and re-announcing them would re-run every consumer's arrival
    /// path on windows that never went anywhere.
    /// </summary>
    [Fact]
    public void AnUncloakedWindowAlreadyTracked_IsNotAnnouncedAgain()
    {
        var native = new FakeNativeWindowSource();
        native.SeedExistingWindow(new IntPtr(7), "Alacritty", Somewhere);
        var workspace = new Win32Workspace(native);
        workspace.Open();

        var announcements = 0;
        workspace.WindowAdded += (_, _) => announcements++;
        native.SimulateWindowUncloakedWithEvent(new IntPtr(7), "Alacritty", Somewhere);

        Assert.Equal(0, announcements);
    }
}
