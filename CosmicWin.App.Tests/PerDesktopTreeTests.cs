using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A layout belongs to a virtual desktop.
/// </summary>
/// <remarks>
/// Reported twice from real use: switching desktops and returning rearranged the windows. Two
/// separate causes, both now closed. <c>Win32Workspace.Poll</c> was reading "absent from the
/// enumeration" as "destroyed" — DWM cloaks every window on the desktop being left — so the tree
/// was dismantled on the way out. That alone only made the tree SURVIVE: with one tree per monitor,
/// every desktop's windows were then laid out together. These facts pin the second half.
/// </remarks>
public sealed class PerDesktopTreeTests
{
    private static readonly Guid DesktopOne = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DesktopTwo = new("22222222-2222-2222-2222-222222222222");

    private static FakeDisplay Display() =>
        new(new IntPtr(1), Rectangle.FromSize(0, 0, 1000, 600), Rectangle.FromSize(0, 0, 1000, 600), 1.0, true);

    [Fact]
    public void EachDesktopKeepsItsOwnTree()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry);

        Assert.True(trees.TryGetTree(DesktopOne, display, out var first));
        Assert.True(trees.TryGetTree(DesktopTwo, display, out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    /// <summary>
    /// The whole point: what the rest of the app sees as "the tree" follows the user between
    /// desktops, and each desktop's layout is still standing when they come back to it.
    /// </summary>
    [Fact]
    public void TheVisibleTreeFollowsTheCurrentDesktop_AndTheOtherIsLeftUntouched()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var current = DesktopOne;
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => current };

        Assert.True(trees.TryGetTree(display, out var onOne));
        onOne!.Root = new LeafNode(new WindowRef(1));

        current = DesktopTwo;
        Assert.True(trees.TryGetTree(display, out var onTwo));
        Assert.Null(onTwo!.Root);

        onTwo.Root = new LeafNode(new WindowRef(2));

        current = DesktopOne;
        Assert.True(trees.TryGetTree(display, out var backOnOne));
        Assert.Same(onOne, backOnOne);
        Assert.Equal(new WindowRef(1), Assert.IsType<LeafNode>(backOnOne!.Root).Window);
    }

    /// <summary>Unset means "there is only one desktop" — exactly how every caller behaved before.</summary>
    [Fact]
    public void WithNoDesktopSource_ThereIsExactlyOneTreePerMonitor()
    {
        var display = Display();
        var trees = new TreeManager([display], display, new WindowRegistry());

        Assert.True(trees.TryGetTree(display, out var a));
        Assert.True(trees.TryGetTree(display, out var b));
        Assert.Same(a, b);
    }

    /// <summary>
    /// A window can arrive on a desktop the user is NOT looking at. It must be filed where it
    /// actually lives, and it must not be repositioned — moving a window nobody can see is at best
    /// wasted work and at worst applies the wrong desktop's geometry to it.
    /// </summary>
    [Fact]
    public void AWindowArrivingOnAHiddenDesktop_IsFiledThere_AndNotRepositioned()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => DesktopOne };
        var workspace = new FakeWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => DesktopTwo,
        };

        var hidden = new RecordingWindow(new IntPtr(0x501), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(hidden);

        Assert.True(trees.TryGetTree(DesktopTwo, display, out var hiddenTree));
        Assert.Equal(new WindowRef(hidden.Handle), Assert.IsType<LeafNode>(hiddenTree!.Root).Window);

        Assert.True(trees.TryGetTree(DesktopOne, display, out var visibleTree));
        Assert.Null(visibleTree!.Root);
        Assert.Equal(0, hidden.SetPositionCallCount);
    }

    [Fact]
    public void AWindowArrivingOnTheVisibleDesktop_IsTiledImmediately()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => DesktopOne };
        var workspace = new FakeWorkspace();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => DesktopOne,
        };

        var visible = new RecordingWindow(new IntPtr(0x502), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(visible);

        Assert.True(trees.TryGetTree(DesktopOne, display, out var tree));
        Assert.Equal(new WindowRef(visible.Handle), Assert.IsType<LeafNode>(tree!.Root).Window);
        Assert.Equal(1, visible.SetPositionCallCount);
    }
}
