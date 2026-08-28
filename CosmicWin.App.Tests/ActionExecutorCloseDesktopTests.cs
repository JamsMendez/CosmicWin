using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// <c>Alt+Shift+Q</c>: close the desktop the user is looking at.
/// </summary>
/// <remarks>
/// <para>
/// The chord ASKS and stops there. Closing a desktop is Windows' own <c>Win+Ctrl+F4</c>, delivered
/// as synthetic input, so the shell answers with an animation rather than a return value -- reading
/// the desktop back on the next line would still name the one being closed. The reconciliation tick
/// already watches for desktops CosmicWin did not switch (Task View, Win+Ctrl+arrow) and rehomes
/// every tracked window on each pass, which is exactly the aftermath a close produces.
/// </para>
/// <para>
/// So there is no handover here, no tree surgery, and deliberately so: doing either would be acting
/// on a desktop set that has not finished changing.
/// </para>
/// </remarks>
public sealed class ActionExecutorCloseDesktopTests
{
    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        public bool IsSupported { get; set; } = true;

        public int Count => 2;

        public int CurrentIndex => 1;

        public Guid CurrentDesktopId => Guid.Empty;

        public string? LastError => null;

        public int CloseCalls { get; private set; }

        public bool TrySwitchTo(int oneBasedIndex) => true;

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex) => true;

        public bool TryCloseCurrentDesktop()
        {
            CloseCalls++;
            return true;
        }
    }

    private sealed class FakeForeground : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;
    }

    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    [Fact]
    public async Task TheChord_AsksTheShellToCloseTheCurrentDesktop()
    {
        var (executor, desktops, _, _) = Build();

        await Close(executor);

        Assert.Equal(1, desktops.CloseCalls);
    }

    /// <summary>
    /// No tree surgery and no handover. The desktop set is still settling when this returns, so the
    /// tick is what picks up the pieces -- acting here would act on a world mid-change.
    /// </summary>
    [Fact]
    public async Task TheChord_LeavesTheTreeAndTheFocusedWindowAlone()
    {
        var (executor, _, window, leaf) = Build();

        await Close(executor);

        Assert.Equal(0, window.TryActivateCallCount);
        Assert.Equal(0, window.SetPositionCallCount);
        Assert.Equal(leaf, executor.ResolveFocusedLeaf());
    }

    /// <summary>The same shape every other desktop chord has: no service wired is a quiet no-op.</summary>
    [Fact]
    public async Task WithNoServiceWired_TheChordIsAQuietNoOp()
    {
        var (executor, _, _, _) = Build();
        executor.VirtualDesktops = null;

        await Close(executor);
    }

    /// <summary>
    /// Traced like every other desktop chord. A chord that did nothing and a chord that was refused
    /// look identical from outside, and this repository has already paid twice for that.
    /// </summary>
    [Fact]
    public async Task WhatHappenedIsRecorded()
    {
        var (executor, _, _, _) = Build();
        var trace = new RecordingDesktopTrace();
        executor.DesktopTrace = trace;

        await Close(executor);

        Assert.Contains(trace.Lines, line => line.Contains("CloseDesktop", StringComparison.Ordinal));
    }

    private static Task Close(ActionExecutor executor) =>
        executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.CloseDesktop), CancellationToken.None)
            .AsTask();

    private static (ActionExecutor Executor, FakeVirtualDesktops Desktops, RecordingWindow Window, LeafNode Leaf)
        Build()
    {
        var leaf = new LeafNode(new WindowRef(new IntPtr(0xC01)));
        var registry = new WindowRegistry();
        var window = new RecordingWindow(leaf.Window.Handle, Rectangle.FromSize(0, 0, 1920, 1080));
        registry.Register(window, leaf);

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);
        var treeManager = new TreeManager([display], display, registry);
        treeManager.TryGetTree(display, out var tree);
        tree!.Root = leaf;

        var desktops = new FakeVirtualDesktops();
        var executor = new ActionExecutor(tree, registry, new FakeForeground { Handle = window.Handle })
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
        };

        return (executor, desktops, window, leaf);
    }
}
