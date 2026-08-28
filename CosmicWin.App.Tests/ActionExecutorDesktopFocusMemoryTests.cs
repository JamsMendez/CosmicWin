using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;

namespace CosmicWin.App.Tests;

/// <summary>
/// Where focus lands when the user WALKS BACK to a desktop they had already been on.
/// </summary>
/// <remarks>
/// <para>
/// Reported from real use: every arrival handed focus to the first tile in the tree, because
/// <see cref="TreeManager.FocusSurvivorOn"/> answers "any window that is not the departing one" and
/// the first leaf is the first thing it finds. A desktop the user had left on their second tile put
/// them back on the first one, every time.
/// </para>
/// <para>
/// The record is keyed by (display, desktop) -- the same pair the layout trees themselves are keyed
/// by -- because "which window was I on" is a question per screen, not per machine.
/// </para>
/// </remarks>
public sealed class ActionExecutorDesktopFocusMemoryTests
{
    private static readonly Guid DesktopA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid DesktopB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private const int A1 = 0xA1;
    private const int A2 = 0xA2;
    private const int B1 = 0xB1;
    private const int B2 = 0xB2;

    /// <summary>Two desktops addressed by index, the way a chord addresses them.</summary>
    private sealed class FakeVirtualDesktops : IVirtualDesktopService
    {
        private readonly Dictionary<int, Guid> _byIndex = new() { [1] = DesktopA, [2] = DesktopB };

        public bool IsSupported => true;

        public int Count => 2;

        public int CurrentIndex => CurrentDesktopId == DesktopA ? 1 : 2;

        public Guid CurrentDesktopId { get; set; } = DesktopA;

        public string? LastError => null;

        public bool TrySwitchTo(int oneBasedIndex)
        {
            if (!_byIndex.TryGetValue(oneBasedIndex, out var desktop))
            {
                return false;
            }

            CurrentDesktopId = desktop;
            return true;
        }

        public bool TryMoveWindowTo(nint windowHandle, int oneBasedIndex) => true;
    }

    private sealed class FakeForegroundWindowSource : IForegroundWindowSource
    {
        public nint Handle { get; set; }

        public nint GetForegroundHandle() => Handle;

        public nint GetActiveWindowOfThreadOwning(nint hwnd) => 0;
    }

    private sealed class RecordingDesktopTrace : CosmicWin.App.Diagnostics.IDesktopTrace
    {
        public List<string> Lines { get; } = [];

        public void Record(string line) => Lines.Add(line);
    }

    private sealed record Harness(
        ActionExecutor Executor,
        FakeForegroundWindowSource Foreground,
        TreeManager Trees,
        FakeDisplay Display,
        Dictionary<nint, RecordingWindow> Windows,
        Dictionary<nint, LeafNode> Leaves,
        List<nint> ActivationOrder,
        RecordingDesktopTrace Trace);

    /// <summary>
    /// The remembered window is the SECOND tile, never the first. A survivor search that keeps
    /// taking the tree's first leaf cannot pass this by accident.
    /// </summary>
    [Fact]
    public async Task ReturningToADesktop_LandsOnTheWindowThatHeldFocusThere()
    {
        var harness = Build();

        // On B, working on its second tile, then away to A and back.
        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B2, harness.ActivationOrder[^1]);
        Assert.Equal(harness.Leaves[B2], Resolve(harness));
    }

    /// <summary>
    /// One record per desktop, not one for the machine. Both desktops are left on their second tile
    /// and both must hand it back -- a single slot would have the last departure answering for both.
    /// </summary>
    [Fact]
    public async Task EachDesktopRemembersItsOwnWindow()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A2);
        await GoTo(harness, desktop: 1, from: B2);
        Assert.Equal(A2, harness.ActivationOrder[^1]);

        await GoTo(harness, desktop: 2, from: A2);
        Assert.Equal(B2, harness.ActivationOrder[^1]);
    }

    /// <summary>
    /// A desktop nobody has left yet has nothing to remember, so the first tile still answers. This
    /// is the pre-existing behaviour, and it must survive as the fallback rather than be replaced.
    /// </summary>
    [Fact]
    public async Task TheFirstVisitToADesktop_StillLandsOnTheFirstTile()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B1, harness.ActivationOrder[^1]);
    }

    /// <summary>
    /// The remembered window closed while the user was away. Recalling a dead handle would activate
    /// nothing and leave the arrival with no focus at all, which is worse than the first tile.
    /// </summary>
    [Fact]
    public async Task AWindowThatDiedWhileAway_IsNotRecalled()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);

        harness.Windows[B2].Kill();

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B1, harness.ActivationOrder[^1]);
    }

    /// <summary>
    /// The remembered window was sent to another desktop while the user was away. The record is
    /// validated against the tree the user is actually looking at, so a rehomed window drops out of
    /// it on its own -- no invalidation hook to forget to wire.
    /// </summary>
    [Fact]
    public async Task AWindowThatLeftForAnotherDesktop_IsNotRecalled()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);

        // What a rehome leaves behind: the window is alive and tracked, it is simply no longer part
        // of the tree for that desktop.
        harness.Trees.TryGetTree(DesktopB, harness.Display, out var tree);
        tree!.Root = harness.Leaves[B1];

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(B1, harness.ActivationOrder[^1]);
    }

    /// <summary>
    /// THE BORDER CASE. The window the arrival sweep exists to clear is precisely the one that held
    /// focus when the desktop was left -- Windows cloaks it without ever delivering
    /// <c>WM_NCACTIVATE(FALSE)</c>, so it keeps painting its own active frame. Landing on it must not
    /// buy it an exemption from the sweep: it is swept like everything else and activated last.
    /// </summary>
    [Fact]
    public async Task TheRecalledLandingWindow_IsSweptTooRatherThanExcluded()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);

        var before = harness.ActivationOrder.Count;
        await GoTo(harness, desktop: 2, from: A1);
        var arrival = harness.ActivationOrder.Skip(before).ToList();

        Assert.Equal(2, arrival.Count(handle => handle == B2));
        Assert.Equal(B2, arrival[^1]);
    }

    /// <summary>
    /// The other half of the same rule: with nothing recalled, the landing window is one the user
    /// was never on, so sweeping it would activate it twice for nothing. Unchanged behaviour, pinned
    /// so the border fix above cannot quietly generalise into every arrival.
    /// </summary>
    [Fact]
    public async Task AFallbackLandingWindow_IsStillExcludedFromTheSweep()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);

        Assert.Equal(1, harness.ActivationOrder.Count(handle => handle == B1));
    }

    /// <summary>The trace names what was recalled, so a supervised run can tell the two paths apart.</summary>
    [Fact]
    public async Task TheHandoverLine_NamesTheRecalledWindow()
    {
        var harness = Build();

        await GoTo(harness, desktop: 2, from: A1);
        await GoTo(harness, desktop: 1, from: B2);
        await GoTo(harness, desktop: 2, from: A1);

        var handovers = harness.Trace.Lines
            .Where(line => line.StartsWith("handover ", StringComparison.Ordinal))
            .ToList();

        Assert.Contains("recalled=0x0 ", handovers[0], StringComparison.Ordinal);
        Assert.Contains($"recalled=0x{B2:X} ", handovers[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks to <paramref name="desktop"/> with <paramref name="from"/> in the foreground, which is
    /// what the OS reports at the instant the chord fires and therefore what gets remembered. Set
    /// explicitly rather than inferred from the last activation: the whole feature turns on WHICH
    /// window was in front when the user left, so a fact must be able to say it out loud.
    /// </summary>
    private static async Task GoTo(Harness harness, int desktop, nint from)
    {
        harness.Foreground.Handle = from;
        await harness.Executor
            .ScheduleAsync(new HotkeyAction(HotkeyActionKind.SwitchDesktop, desktop), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the cache through the public surface, with the foreground pointed at nobody.</summary>
    private static LeafNode? Resolve(Harness harness)
    {
        harness.Foreground.Handle = new IntPtr(0x404);
        return harness.Executor.ResolveFocusedLeaf();
    }

    /// <summary>
    /// One monitor, two desktops, two tiles each. Two is the minimum that can tell "the window the
    /// user was on" apart from "the first window in the tree".
    /// </summary>
    private static Harness Build()
    {
        var registry = new WindowRegistry();
        var order = new List<nint>();
        var windows = new Dictionary<nint, RecordingWindow>();
        var leaves = new Dictionary<nint, LeafNode>();

        GroupNode Row(params nint[] handles)
        {
            var group = new GroupNode(SplitAxis.Horizontal) { GroupLength = 1920 };
            foreach (var handle in handles)
            {
                var leaf = new LeafNode(new WindowRef(handle)) { Parent = group };
                group.Children.Add(leaf);
                group.Sizes.Add(1920 / handles.Length);

                var window = new RecordingWindow(handle, Rectangle.FromSize(0, 0, 960, 1080))
                {
                    ActivationLog = order,
                };
                registry.Register(window, leaf);
                windows[handle] = window;
                leaves[handle] = leaf;
            }

            return group;
        }

        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1080), 1.0, true);

        var desktops = new FakeVirtualDesktops();
        var treeManager = new TreeManager([display], display, registry)
        {
            CurrentDesktop = () => desktops.CurrentDesktopId,
        };

        treeManager.TryGetTree(DesktopA, display, out var treeA);
        treeA!.Root = Row(A1, A2);

        treeManager.TryGetTree(DesktopB, display, out var treeB);
        treeB!.Root = Row(B1, B2);

        var foreground = new FakeForegroundWindowSource { Handle = A1 };
        var trace = new RecordingDesktopTrace();
        var executor = new ActionExecutor(treeA, registry, foreground)
        {
            WorkArea = new Rect(0, 0, 1920, 1080),
            TreeManager = treeManager,
            VirtualDesktops = desktops,
            DesktopTrace = trace,
        };

        return new Harness(
            executor, foreground, treeManager, display, windows, leaves, order, trace);
    }
}
