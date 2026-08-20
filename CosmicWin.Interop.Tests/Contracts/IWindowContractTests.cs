using CosmicWin.Interop;
using CosmicWin.Interop.Tests.TestDoubles;

namespace CosmicWin.Interop.Tests.Contracts;

public class IWindowContractTests
{
    [Fact]
    public void SetPosition_UpdatesBounds()
    {
        var window = new FakeWindow(new IntPtr(1), "Notepad", Rectangle.FromSize(0, 0, 800, 600));

        window.SetPosition(Rectangle.FromSize(100, 50, 400, 300));

        Assert.Equal(Rectangle.FromSize(100, 50, 400, 300), window.Bounds);
    }

    [Fact]
    public void Equals_ComparesByHandle_NotByTitleOrBounds()
    {
        IWindow a = new FakeWindow(new IntPtr(42), "A", Rectangle.FromSize(0, 0, 100, 100));
        IWindow b = new FakeWindow(new IntPtr(42), "B", Rectangle.FromSize(10, 10, 50, 50));
        IWindow c = new FakeWindow(new IntPtr(43), "A", Rectangle.FromSize(0, 0, 100, 100));

        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void DeadWindow_ReturnsDefaultValidValues_NeverThrows()
    {
        var window = new FakeWindow(new IntPtr(7), "Explorer", Rectangle.FromSize(0, 0, 640, 480));

        window.Kill();

        Assert.False(window.IsAlive);
        Assert.Equal(string.Empty, window.Title);
        Assert.Equal(Rectangle.Empty, window.Bounds);
    }

    [Fact]
    public void CanReposition_StartsTrue_ForANewlyTrackedWindow()
    {
        var window = new FakeWindow(new IntPtr(8), "Notepad", Rectangle.FromSize(0, 0, 400, 300));

        Assert.True(window.CanReposition);
    }

    [Fact]
    public void SetPosition_Failure_MarksWindowNonRepositionable_WithoutThrowing()
    {
        // Threat matrix: "Cross-process window manipulation" — every IWindow implementation,
        // not just Win32Window, must honor this: a failed reposition degrades the window
        // rather than throwing or crashing the caller.
        var window = new FakeWindow(new IntPtr(9), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200));
        window.FailNextSetPosition();

        var exception = Record.Exception(() => window.SetPosition(Rectangle.FromSize(500, 500, 200, 200)));

        Assert.Null(exception);
        Assert.False(window.CanReposition);
    }

    [Fact]
    public void TryActivate_ReturnsTrue_OnSuccess()
    {
        var window = new FakeWindow(new IntPtr(10), "Notepad", Rectangle.FromSize(0, 0, 400, 300));

        Assert.True(window.TryActivate());
    }

    [Fact]
    public void TryActivate_Failure_ReturnsFalse_WithoutThrowing()
    {
        // Threat matrix: activation of a higher-integrity/protected window can fail — every
        // IWindow implementation must degrade to a returned false rather than throwing.
        var window = new FakeWindow(new IntPtr(11), "ProtectedApp", Rectangle.FromSize(0, 0, 200, 200));
        window.FailNextActivate();

        bool activated = true;
        var exception = Record.Exception(() => activated = window.TryActivate());

        Assert.Null(exception);
        Assert.False(activated);
    }
}
