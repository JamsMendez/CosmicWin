using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A window CosmicWin is merely SEEING for the first time stays where it is.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use: restart CosmicWin while more than one desktop holds windows, then move
/// between desktops, and the windows from the other desktops are dragged to the one CosmicWin
/// thinks the user is on.
/// </para>
/// <para>
/// The redirect doing the dragging is <see cref="ArrivingWindowDesktopTests"/>'s feature, and it is
/// right: a window BORN on the wrong desktop belongs with the user. The defect is that
/// <c>WindowAdded</c> could not tell that apart from an ADOPTION. <c>Win32Workspace.TryAddWindow</c>
/// raises the identical event from three places -- <c>Open()</c> and <c>Poll()</c>, which adopt
/// windows that already existed, and the <c>Created</c> hook, which is the only genuine birth.
/// </para>
/// <para>
/// Adoption of another desktop's windows is not exotic, it is guaranteed. <c>IsTrackable</c> rejects
/// cloaked windows and DWM cloaks every window on a desktop the user is not looking at, so a
/// starting CosmicWin can only ever adopt the visible desktop. Every other desktop's windows are
/// adopted later, at the moment the user walks over to them and they uncloak -- and the tick polls
/// BEFORE it refreshes which desktop the user is on, so "the user's desktop" still named the one
/// they had just left. A window that had never moved was therefore moved, to a desktop the user was
/// no longer on.
/// </para>
/// <para>
/// Same shape as <c>IsUserGesture</c> on the bounds-changed event, and for the same reason: two
/// arrivals are the same fact about a window appearing and a completely different fact about
/// intent. Only a birth carries a decision worth overruling.
/// </para>
/// </remarks>
public sealed class AdoptedWindowDesktopTests
{
    private static readonly Guid UserDesktop = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ElsewhereDesktop = new("22222222-2222-2222-2222-222222222222");

    private static readonly Rectangle Screen = Rectangle.FromSize(0, 0, 1000, 600);

    private static FakeDisplay Display() => new(new IntPtr(1), Screen, Screen, 1.0, true);

    private sealed record Harness(
        MultiMonitorWorkspaceAdapter Adapter, FakeWorkspace Workspace, TreeManager Trees,
        FakeDisplay Display, List<(nint Handle, Guid Desktop)> Sent);

    /// <summary>
    /// The reported state exactly: the windows live on <see cref="ElsewhereDesktop"/>, and CosmicWin
    /// still believes the user is on <see cref="UserDesktop"/> because the poll runs before the tick
    /// refreshes it.
    /// </summary>
    private static Harness StaleUserDesktop()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => UserDesktop };
        var workspace = new FakeWorkspace();
        var sent = new List<(nint, Guid)>();

        var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => ElsewhereDesktop,
            ResolveUserDesktop = () => UserDesktop,
            SendWindowToDesktop = (handle, desktop) =>
            {
                sent.Add((handle, desktop));
                return true;
            },
        };

        return new Harness(adapter, workspace, trees, display, sent);
    }

    /// <summary>The whole report in one fact: nothing is moved.</summary>
    [Fact]
    public void AWindowAdoptedOnAnotherDesktop_IsNeverMoved()
    {
        var h = StaleUserDesktop();
        using var adapter = h.Adapter;

        var existing = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(10, 10, 400, 300));
        h.Workspace.RaiseWindowAdded(existing, WindowArrival.Adopted);

        Assert.Empty(h.Sent);
    }

    /// <summary>"que se acomode en su propio tree" -- filed where it lives, and laid out there.</summary>
    [Fact]
    public void AWindowAdoptedOnAnotherDesktop_IsTiledInThatDesktopsOwnTree()
    {
        var h = StaleUserDesktop();
        using var adapter = h.Adapter;

        var existing = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(10, 10, 400, 300));
        h.Workspace.RaiseWindowAdded(existing, WindowArrival.Adopted);

        Assert.True(h.Trees.TryGetTree(ElsewhereDesktop, h.Display, out var theirs));
        Assert.Equal(new WindowRef(existing.Handle), Assert.IsType<LeafNode>(theirs!.Root).Window);

        Assert.True(h.Trees.TryGetTree(UserDesktop, h.Display, out var mine));
        Assert.Null(mine!.Root);

        Assert.Equal(Screen, existing.LastSetPosition);
    }

    /// <summary>
    /// Two adopted windows on the same foreign desktop share that desktop's tree, rather than one
    /// of them being dragged away and each ending up alone.
    /// </summary>
    [Fact]
    public void SeveralWindowsAdoptedOnAnotherDesktop_ShareThatDesktopsTree()
    {
        var h = StaleUserDesktop();
        using var adapter = h.Adapter;

        var first = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(10, 10, 400, 300));
        var second = new RecordingWindow(new IntPtr(0x802), Rectangle.FromSize(20, 20, 400, 300));
        h.Workspace.RaiseWindowAdded(first, WindowArrival.Adopted);
        h.Workspace.RaiseWindowAdded(second, WindowArrival.Adopted);

        Assert.Empty(h.Sent);
        Assert.True(h.Trees.TryGetTree(ElsewhereDesktop, h.Display, out var theirs));
        var root = Assert.IsType<GroupNode>(theirs!.Root);
        Assert.Equal(
            [new WindowRef(first.Handle), new WindowRef(second.Handle)],
            root.Children.Cast<LeafNode>().Select(leaf => leaf.Window));
    }

    /// <summary>
    /// The guard that keeps the fix honest. A window genuinely BORN on another desktop must still be
    /// brought to the user -- that is a reported defect's fix, and narrowing the redirect must not
    /// quietly undo it.
    /// </summary>
    [Fact]
    public void AWindowBornOnAnotherDesktop_IsStillSentToTheUser()
    {
        var h = StaleUserDesktop();
        using var adapter = h.Adapter;

        var born = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(10, 10, 400, 300));
        h.Workspace.RaiseWindowAdded(born, WindowArrival.Created);

        Assert.Equal([(born.Handle, UserDesktop)], h.Sent);
    }

    /// <summary>
    /// A birth is what an unqualified announcement means, so every caller written before this
    /// distinction existed keeps the behaviour it was written for.
    /// </summary>
    [Fact]
    public void AnUnqualifiedAnnouncement_IsTreatedAsABirth()
    {
        var h = StaleUserDesktop();
        using var adapter = h.Adapter;

        var born = new RecordingWindow(new IntPtr(0x801), Rectangle.FromSize(10, 10, 400, 300));
        h.Workspace.RaiseWindowAdded(born);

        Assert.Equal([(born.Handle, UserDesktop)], h.Sent);
    }
}
