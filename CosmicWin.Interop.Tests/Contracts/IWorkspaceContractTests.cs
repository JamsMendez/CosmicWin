using CosmicWin.Interop;
using CosmicWin.Interop.Tests.TestDoubles;

namespace CosmicWin.Interop.Tests.Contracts;

public class IWorkspaceContractTests
{
    [Fact]
    public void Open_MarksWorkspaceAsOpen()
    {
        var workspace = new FakeWorkspace();
        Assert.False(workspace.IsOpen);

        workspace.Open();

        Assert.True(workspace.IsOpen);
    }

    [Fact]
    public void AddingAWindow_RaisesWindowAdded_AndAppearsInSnapshot()
    {
        var workspace = new FakeWorkspace();
        workspace.Open();
        WindowEventArgs? received = null;
        workspace.WindowAdded += (_, e) => received = e;

        var window = workspace.AddWindow(new IntPtr(1), "Notepad", Rectangle.FromSize(0, 0, 300, 200));

        Assert.NotNull(received);
        Assert.Equal(window, received!.Window);
        Assert.Contains(workspace.Snapshot, w => w.Handle == window.Handle);
    }

    [Fact]
    public void RemovingAWindow_RaisesWindowRemoved_AndLeavesSnapshot()
    {
        var workspace = new FakeWorkspace();
        workspace.Open();
        var window = workspace.AddWindow(new IntPtr(2), "Calculator", Rectangle.FromSize(0, 0, 100, 100));
        WindowEventArgs? received = null;
        workspace.WindowRemoved += (_, e) => received = e;

        workspace.RemoveWindow(window.Handle);

        Assert.NotNull(received);
        Assert.DoesNotContain(workspace.Snapshot, w => w.Handle == window.Handle);
    }

    [Fact]
    public void MovingAWindow_RaisesWindowBoundsChanged_WithNewBounds()
    {
        var workspace = new FakeWorkspace();
        workspace.Open();
        var window = workspace.AddWindow(new IntPtr(3), "Paint", Rectangle.FromSize(0, 0, 200, 200));
        WindowEventArgs? received = null;
        workspace.WindowBoundsChanged += (_, e) => received = e;

        workspace.MoveWindow(window.Handle, Rectangle.FromSize(500, 500, 200, 200));

        Assert.NotNull(received);
        Assert.Equal(Rectangle.FromSize(500, 500, 200, 200), received!.Window.Bounds);
    }
}
