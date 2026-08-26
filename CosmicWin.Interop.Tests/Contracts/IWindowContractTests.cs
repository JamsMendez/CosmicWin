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

    /// <summary>
    /// <see cref="IWindow.TryActivate"/> is the DERIVED reading of <see cref="IWindow.Activate"/>,
    /// never an independent one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bool survives because dozens of call sites legitimately only need "did focus move".
    /// What must not survive is an implementation free to answer it separately: two activation
    /// methods that can disagree is strictly worse than the single flattened one this replaces,
    /// because then the log and the behaviour come from different code.
    /// </para>
    /// <para>
    /// Pinned as a contract rather than left to a comment, because the rule is only worth anything
    /// if the next implementation of <see cref="IWindow"/> inherits it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TryActivate_IsExactly_ActivateConfirmed()
    {
        var window = new FakeWindow(new IntPtr(12), "Notepad", Rectangle.FromSize(0, 0, 400, 300));

        foreach (var outcome in Enum.GetValues<ActivationOutcome>())
        {
            window.NextActivation = outcome;
            var reported = window.Activate();

            window.NextActivation = outcome;
            Assert.Equal(reported.Confirmed(), window.TryActivate());
        }
    }

    /// <summary>
    /// Which outcomes count as the foreground having genuinely moved -- the one place that judgement
    /// is made, so it cannot drift between the Win32 source and anything else that reads an outcome.
    /// </summary>
    /// <remarks>
    /// A timeout confirms NOTHING. It is not the OS refusing; it is our own budget expiring before
    /// the worker was scheduled, so it answers false for the same reason a refusal does while
    /// staying a different fact on the line above.
    /// </remarks>
    [Theory]
    [InlineData(ActivationOutcome.AlreadyForeground, true)]
    [InlineData(ActivationOutcome.Direct, true)]
    [InlineData(ActivationOutcome.AttachedInput, true)]
    [InlineData(ActivationOutcome.InputUnlocked, true)]
    [InlineData(ActivationOutcome.Failed, false)]
    [InlineData(ActivationOutcome.TimedOut, false)]
    public void Confirmed_IsTrue_OnlyWhenTheOsConfirmedTheForegroundMoved(
        ActivationOutcome outcome, bool expected)
    {
        Assert.Equal(expected, outcome.Confirmed());
    }
}
