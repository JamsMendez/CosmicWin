using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// A window opens on the desktop the user is on.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use and then measured in the desktop trace: the user switched to desktop 2,
/// launched a browser, and both the window and the user ended up on desktop 1. The trace records
/// every switch CosmicWin issues and shows NONE between the two — Windows moved them, not us.
/// </para>
/// <para>
/// Windows decides where a new window is born, and an application that already owns a window
/// elsewhere can have the next one born beside it. CosmicWin previously only ever FILED an arriving
/// window under whatever desktop the shell named, so it inherited that decision. Matching the
/// reference implementation's behaviour means overruling it: the window is sent to the user, and the
/// user is not dragged after the window.
/// </para>
/// <para>
/// "The desktop the user is on" deliberately is NOT the shell's live answer. By the time the window
/// arrives the shell may already have taken the user somewhere else, which is the very defect being
/// fixed — so the question is answered from what was true just BEFORE the window appeared.
/// </para>
/// </remarks>
public sealed class ArrivingWindowDesktopTests
{
    private static readonly Guid UserDesktop = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ElsewhereDesktop = new("22222222-2222-2222-2222-222222222222");

    private static FakeDisplay Display() =>
        new(new IntPtr(1), Rectangle.FromSize(0, 0, 1000, 600), Rectangle.FromSize(0, 0, 1000, 600), 1.0, true);

    [Fact]
    public void AWindowBornOnAnotherDesktop_IsSentToTheOneTheUserIsOn_AndTiledThere()
    {
        var display = Display();
        var registry = new WindowRegistry();

        // The shell has already dragged the view away, which is exactly the reported symptom.
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => ElsewhereDesktop };
        var workspace = new FakeWorkspace();

        var sent = new List<(nint Handle, Guid Desktop)>();
        var lives = new Dictionary<nint, Guid>();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = handle => lives.TryGetValue(handle, out var id) ? id : ElsewhereDesktop,
            ResolveUserDesktop = () => UserDesktop,
            SendWindowToDesktop = (handle, desktop) =>
            {
                sent.Add((handle, desktop));
                lives[handle] = desktop;
                return true;
            },
        };

        var born = new RecordingWindow(new IntPtr(0x701), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(born);

        Assert.Equal([(born.Handle, UserDesktop)], sent);

        Assert.True(trees.TryGetTree(UserDesktop, display, out var mine));
        Assert.Equal(new WindowRef(born.Handle), Assert.IsType<LeafNode>(mine!.Root).Window);

        Assert.True(trees.TryGetTree(ElsewhereDesktop, display, out var theirs));
        Assert.Null(theirs!.Root);

        Assert.Equal(Rectangle.FromSize(0, 0, 1000, 600), born.LastSetPosition);
    }

    /// <summary>The ordinary case must cost nothing: no move, no switch, no extra shell traffic.</summary>
    [Fact]
    public void AWindowBornWhereTheUserAlreadyIs_IsNotMoved()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => UserDesktop };
        var workspace = new FakeWorkspace();

        var sent = new List<(nint Handle, Guid Desktop)>();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => UserDesktop,
            ResolveUserDesktop = () => UserDesktop,
            SendWindowToDesktop = (handle, desktop) =>
            {
                sent.Add((handle, desktop));
                return true;
            },
        };

        workspace.RaiseWindowAdded(new RecordingWindow(new IntPtr(0x702), Rectangle.FromSize(0, 0, 400, 300)));

        Assert.Empty(sent);
    }

    /// <summary>
    /// A refused move must not be papered over. Filing the window where we WANTED it to be would
    /// leave the layout describing a desktop the window is not on — the same class of lie the empty
    /// desktop id already taught this code not to tell.
    /// </summary>
    [Fact]
    public void WhenTheShellRefusesTheMove_TheWindowIsFiledWhereItActuallyIs()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => ElsewhereDesktop };
        var workspace = new FakeWorkspace();

        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => ElsewhereDesktop,
            ResolveUserDesktop = () => UserDesktop,
            SendWindowToDesktop = (_, _) => false,
        };

        var born = new RecordingWindow(new IntPtr(0x703), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(born);

        Assert.True(trees.TryGetTree(ElsewhereDesktop, display, out var whereItIs));
        Assert.Equal(new WindowRef(born.Handle), Assert.IsType<LeafNode>(whereItIs!.Root).Window);

        Assert.True(trees.TryGetTree(UserDesktop, display, out var whereItIsNot));
        Assert.Null(whereItIsNot!.Root);
    }

    /// <summary>
    /// <see cref="Guid.Empty"/> means the shell would not say, and it already means "the desktop
    /// being viewed" everywhere else here. Redirecting on an unknown answer would move windows on a
    /// guess -- and the shell answers empty for every window that is merely mid-creation.
    /// </summary>
    [Fact]
    public void AWindowWhoseDesktopTheShellWillNotName_IsNeverMoved()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => UserDesktop };
        var workspace = new FakeWorkspace();

        var sent = new List<(nint Handle, Guid Desktop)>();
        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => Guid.Empty,
            ResolveUserDesktop = () => UserDesktop,
            SendWindowToDesktop = (handle, desktop) =>
            {
                sent.Add((handle, desktop));
                return true;
            },
        };

        var born = new RecordingWindow(new IntPtr(0x704), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(born);

        Assert.Empty(sent);
        Assert.True(trees.TryGetTree(UserDesktop, display, out var visible));
        Assert.Equal(new WindowRef(born.Handle), Assert.IsType<LeafNode>(visible!.Root).Window);
    }

    /// <summary>
    /// Unwired -- which is every test that predates this and every build without virtual desktops --
    /// arriving windows are filed exactly as they were before.
    /// </summary>
    [Fact]
    public void WithNothingWiredToMoveWindows_AnArrivingWindowIsFiledWhereTheShellSaysItIs()
    {
        var display = Display();
        var registry = new WindowRegistry();
        var trees = new TreeManager([display], display, registry) { CurrentDesktop = () => UserDesktop };
        var workspace = new FakeWorkspace();

        using var adapter = new MultiMonitorWorkspaceAdapter(
            workspace, trees, registry, () => ExceptionList.Empty, () => false, () => null)
        {
            ResolveWindowDesktop = _ => ElsewhereDesktop,
        };

        var born = new RecordingWindow(new IntPtr(0x705), Rectangle.FromSize(0, 0, 400, 300));
        workspace.RaiseWindowAdded(born);

        Assert.True(trees.TryGetTree(ElsewhereDesktop, display, out var whereItIs));
        Assert.Equal(new WindowRef(born.Handle), Assert.IsType<LeafNode>(whereItIs!.Root).Window);
    }
}
