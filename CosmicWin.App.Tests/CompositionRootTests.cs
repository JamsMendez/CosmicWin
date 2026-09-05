using System.Threading.Channels;
using CosmicWin.App.Input;
using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// Task 2.17: writing a <see cref="HotkeyAction"/> to the wired pipeline's dispatcher channel
/// reaches a recording <see cref="ITilingEngine"/> fake with the correct <see cref="Direction"/>
/// and dispatched kind, end-to-end through <see cref="ActionDispatcher"/> -&gt; <see
/// cref="ActionExecutor"/>, using a fake engine/registry/foreground -- no live desktop required.
/// Before <see cref="CompositionRoot"/> existed, nothing but
/// tests ever constructed a wired <see cref="ActionDispatcher"/>/<see cref="ActionExecutor"/> pair.
/// </summary>
public sealed class CompositionRootTests
{
    private static (RecordingTilingEngine Engine, ActionDispatcher Dispatcher) BuildWiredPipeline(nint focusedHandle)
    {
        var leaf = new LeafNode(new WindowRef(focusedHandle));
        var engine = new RecordingTilingEngine();
        var registry = new WindowRegistry();
        var window = new RecordingWindow(focusedHandle, Rectangle.Empty);
        registry.Register(window, leaf);
        var foreground = new StaticForegroundWindowSource(focusedHandle);

        var (dispatcher, _) = CompositionRoot.Build(engine, registry, foreground, workArea: default);
        return (engine, dispatcher);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task WiredPipeline_MoveRight_ReachesRecordingEngine_WithDirectionRight()
    {
        var (engine, dispatcher) = BuildWiredPipeline(new IntPtr(101));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = dispatcher.RunAsync(cts.Token);

        Assert.True(dispatcher.Writer.TryWrite(new HotkeyAction(HotkeyActionKind.MoveRight)));

        var observed = await WaitUntil(() => engine.MoveNodeCallCount > 0, TimeSpan.FromSeconds(2));

        await dispatcher.DisposeAsync();
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.True(observed);
        Assert.Equal(1, engine.MoveNodeCallCount);
        Assert.Equal(Direction.Right, engine.LastMoveDirection);
        Assert.Equal(0, engine.ResizeNodeCallCount);
    }

    [Fact]
    public async Task WiredPipeline_ResizeDown_ReachesRecordingEngine_WithDirectionDown()
    {
        var (engine, dispatcher) = BuildWiredPipeline(new IntPtr(202));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = dispatcher.RunAsync(cts.Token);

        Assert.True(dispatcher.Writer.TryWrite(new HotkeyAction(HotkeyActionKind.ResizeDown)));

        var observed = await WaitUntil(() => engine.ResizeNodeCallCount > 0, TimeSpan.FromSeconds(2));

        await dispatcher.DisposeAsync();
        await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.True(observed);
        Assert.Equal(1, engine.ResizeNodeCallCount);
        Assert.Equal(Direction.Down, engine.LastResizeDirection);
        Assert.Equal(0, engine.MoveNodeCallCount);
    }

    /// <summary>
    /// Verify-report #21 CRITICAL C1: proves the composition pipeline actually assigns a
    /// non-zero, display-derived work area to the executor -- previously nothing in production
    /// ever did, so the default <c>Rect(0,0,0,0)</c> zeroed every window on the first
    /// Move/Toggle/Resize chord. Uses a fake <see cref="IDisplay"/> (see <see
    /// cref="WorkAreaResolverTests"/> for the resolver's own pure conversion tests); no live
    /// desktop required, consistent with the rest of this file.
    /// </summary>
    [Fact]
    public void Build_AssignsExecutorWorkArea_FromResolvedDisplayWorkArea_NonZero()
    {
        var display = new FakeDisplay(
            new IntPtr(1), Rectangle.FromSize(0, 0, 1920, 1080), Rectangle.FromSize(0, 0, 1920, 1040), 1.0, isPrimary: true);
        var resolvedWorkArea = WorkAreaResolver.Resolve(display);

        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), new WindowRegistry(), new StaticForegroundWindowSource(IntPtr.Zero), resolvedWorkArea);

        Assert.NotEqual(default, executor.WorkArea);
        Assert.Equal(new Rect(0, 0, 1920, 1040), executor.WorkArea);
    }

    /// <summary>
    /// The composition-root joint at <c>App.xaml.cs</c> that wires <c> =>
    /// _exceptionStore.Current</c> into <see cref="WorkspaceSessionAdapter"/> had zero coverage --
    /// a mutation replacing that delegate with a constant <see cref="ExceptionList.Empty"/> passed
    /// the entire suite, silently discarding the user's whole on-disk exception list. Extracted to
    /// <see cref="CompositionRoot.BuildSessionAdapter"/> (same seam pattern as <c>workArea</c>) so
    /// this joint is testable outside the untestable WPF <see cref="App"/> class.
    /// </summary>
    /// <summary>
    /// The failure sink reaches the dispatcher, so a chord that throws is REPORTED and not merely
    /// survived.
    /// </summary>
    /// <remarks>
    /// This suite already learned the lesson this fact exists for: a proven mechanism nobody proved
    /// was CALLED is half a feature. The pump surviving a throw has its own facts; without this one,
    /// nothing says the composition ever handed it somewhere to report the throw TO, and a silent
    /// drop looks exactly like a chord the user imagined pressing.
    /// </remarks>
    [Fact]
    public async Task Build_PassesTheFailureSinkToTheDispatcher()
    {
        // A TRACKED foreground, or the chord is dropped before it ever reaches the engine and this
        // fact would pass an empty list off as proof of nothing.
        const nint focusedHandle = 0xC1;
        var registry = new WindowRegistry();
        registry.Register(
            new RecordingWindow(focusedHandle, Rectangle.Empty), new LeafNode(new WindowRef(focusedHandle)));

        var failures = new List<HotkeyAction>();
        var (dispatcher, _) = CompositionRoot.Build(
            new ThrowingTilingEngine(), registry, new StaticForegroundWindowSource(focusedHandle),
            workArea: new Rect(0, 0, 1920, 1080),
            onActionFailed: (action, _) => failures.Add(action));

        await using (dispatcher)
        {
            dispatcher.Writer.TryWrite(new(HotkeyActionKind.ToggleOrientation));
            dispatcher.Writer.Complete();
            await dispatcher.RunAsync(CancellationToken.None);
        }

        Assert.Equal(HotkeyActionKind.ToggleOrientation, Assert.Single(failures).Kind);
    }

    /// <summary>
    /// Blows up the moment the executor touches the tree, standing in for the real throw sites --
    /// <c>TreeManager</c>'s unknown-node-type and empty-group guards, which the desktop path walks
    /// through on every chord.
    /// </summary>
    private sealed class ThrowingTilingEngine : ITilingEngine
    {
        private static Exception Corrupt() => new InvalidOperationException("Unknown node type");

        public FocusResult NextFocus(Direction direction, LeafNode focused) => throw Corrupt();

        public bool MoveNode(Direction direction, Node focused) => throw Corrupt();

        public bool ToggleAxis(Node focused) => throw Corrupt();

        public bool ResizeNode(Direction direction, Node focused, double step = LayoutTree.DefaultResizeStep, Func<Node, SplitAxis, (int Min, int Max)>? limitsOf = null) =>
            throw Corrupt();

        public IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea) => throw Corrupt();

        public bool Remove(Node focused) => throw Corrupt();
    }

    [Fact]
    public void BuildSessionAdapter_ReadsExceptionStoreCurrent_ExcludingManuallyListedWindow()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), registry, new StaticForegroundWindowSource(IntPtr.Zero),
            new Rect(0, 0, 1920, 1080));
        var exceptionStore = new ExceptionListStore(
            new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Spotify.exe")]));

        using var adapter = CompositionRoot.BuildSessionAdapter(
            workspace, tree, registry, executor, exceptionStore, isPaused: () => false);

        var spotify = new RecordingWindow(new IntPtr(700), Rectangle.FromSize(0, 0, 800, 600), processName: "Spotify.exe");
        workspace.RaiseWindowAdded(spotify);

        Assert.Null(tree.Root);
        Assert.False(registry.TryGetWindow(spotify.Handle, out _));
    }

    /// <summary>
    /// Triangulation companion: a window NOT on the store's exception list must still be added and
    /// arranged against the SAME executor work area <see cref="Build"/> assigned -- proving
    /// <see cref="CompositionRoot.BuildSessionAdapter"/> genuinely reads <c>exceptions.Current</c>
    /// each call (not merely returning a constant that happens to differ from the excluded case).
    /// </summary>
    [Fact]
    public void BuildSessionAdapter_NormalWindow_NotOnExceptionList_StillAddedAndArranged()
    {
        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), registry, new StaticForegroundWindowSource(IntPtr.Zero),
            new Rect(0, 0, 1920, 1080));
        var exceptionStore = new ExceptionListStore(
            new ExceptionList([new ExceptionRule(ExceptionRuleKind.ProcessName, "Spotify.exe")]));

        using var adapter = CompositionRoot.BuildSessionAdapter(
            workspace, tree, registry, executor, exceptionStore, isPaused: () => false);

        var normal = new RecordingWindow(new IntPtr(701), Rectangle.FromSize(0, 0, 1920, 1080), processName: "chrome.exe");
        workspace.RaiseWindowAdded(normal);

        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(new WindowRef(normal.Handle), leaf.Window);
        Assert.Equal(1, normal.SetPositionCallCount);
        Assert.True(registry.TryGetWindow(normal.Handle, out var found));
        Assert.Same(normal, found);
    }

    /// <summary>Proves <see cref="CompositionRoot.BuildTrayMenuController"/> wires TogglePause against the REAL <see cref="LowLevelKeyboardHook.IsPaused"/> seam, not a local fake.</summary>
    [Fact]
    public void BuildTrayMenuController_TogglePause_FlipsRealHookIsPaused()
    {
        using var hook = new LowLevelKeyboardHook(Channel.CreateUnbounded<HotkeyAction>().Writer);
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);
        var controller = CompositionRoot.BuildTrayMenuController(hook, exceptionStore, () => ExceptionList.Empty, () => true, _ => { }, () => { });

        Assert.False(controller.IsPaused);
        var next = controller.TogglePause();

        Assert.True(next);
        Assert.True(hook.IsPaused);
    }

    /// <summary>Salir's trigger -- <see cref="CompositionRoot.BuildTrayMenuController"/> wires Exit onto the injected exit action exactly once.</summary>
    [Fact]
    public void BuildTrayMenuController_Exit_InvokesInjectedExitAction_ExactlyOnce()
    {
        using var hook = new LowLevelKeyboardHook(Channel.CreateUnbounded<HotkeyAction>().Writer);
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);
        var exitCount = 0;
        var controller = CompositionRoot.BuildTrayMenuController(hook, exceptionStore, () => ExceptionList.Empty, () => true, _ => { }, () => exitCount++);

        controller.Exit();

        Assert.Equal(1, exitCount);
    }

    /// <summary>
    /// Proves the tray Reload trigger is
    /// real end-to-end, through <see cref="TrayMenuController"/> and the SAME <see
    /// cref="ExceptionListStore"/> a real <see cref="WorkspaceSessionAdapter"/> reads from -- not
    /// just <see cref="ExceptionListStore"/> in isolation. Mirrors WE-3's own scenario
    /// ("Removing an exception restores tiling"). Uses an isolated temp file (matching the existing
    /// <c>ExceptionListFileTests</c> precedent) so no test ever touches the real on-disk
    /// <c>%LOCALAPPDATA%</c> exception list -- <paramref name="loadExceptions"/>-style injection is
    /// what makes that isolation possible without changing the production wiring's behavior.
    /// </summary>
    [Fact]
    public void BuildTrayMenuController_Reload_ReReadsInjectedSource_AndSessionAdapterSeesUpdatedExclusion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cosmicwin-tray-reload-{Guid.NewGuid():N}.conf");
        File.WriteAllText(path, "process:Spotify.exe\n");
        try
        {
            var workspace = new FakeWorkspace();
            var tree = new LayoutTree();
            var registry = new WindowRegistry();
            var (_, executor) = CompositionRoot.Build(
                new RecordingTilingEngine(), registry, new StaticForegroundWindowSource(IntPtr.Zero),
                new Rect(0, 0, 1920, 1080));
            var exceptionStore = new ExceptionListStore(ExceptionListFile.Load(path));
            using var adapter = CompositionRoot.BuildSessionAdapter(
                workspace, tree, registry, executor, exceptionStore, isPaused: () => false);
            using var hook = new LowLevelKeyboardHook(Channel.CreateUnbounded<HotkeyAction>().Writer);
            var controller = CompositionRoot.BuildTrayMenuController(
                hook, exceptionStore, () => ExceptionListFile.Load(path), () => true, _ => { }, () => { });

            var before = new RecordingWindow(new IntPtr(910), Rectangle.FromSize(0, 0, 800, 600), processName: "Spotify.exe");
            workspace.RaiseWindowAdded(before);
            Assert.Null(tree.Root);

            File.WriteAllText(path, string.Empty);
            controller.Reload();

            var after = new RecordingWindow(new IntPtr(911), Rectangle.FromSize(0, 0, 800, 600), processName: "Spotify.exe");
            workspace.RaiseWindowAdded(after);

            var leaf = Assert.IsType<LeafNode>(tree.Root);
            Assert.Equal(new WindowRef(after.Handle), leaf.Window);
            Assert.Equal(1, after.SetPositionCallCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Closes 's mutation-surviving gap where deleting the <c>isPaused</c>
    /// argument from <c>App.xaml.cs</c>'s <see cref="CompositionRoot.BuildSessionAdapter"/> call
    /// compiled cleanly (the parameter is optional, defaulting to never-paused) and left the whole
    /// suite green. <see cref="CompositionRoot.BuildPauseGatedSession"/> wires the SAME hook
    /// instance's <see cref="LowLevelKeyboardHook.IsPaused"/> into the adapter's pause gate as a
    /// mandatory constructor argument, so App.xaml.cs no longer has an isPaused argument it could
    /// silently drop. This fact drives a REAL <see cref="LowLevelKeyboardHook"/> (via <see
    /// cref="FakeKeyboardHookPlatform"/>, the same seam <c>KeyboardHookTests</c> uses) end to end
    /// through its actual <see cref="KeyboardEventProcessor"/>, proving that a SINGLE write of
    /// <c>hook.IsPaused</c> blocks BOTH a real chord match AND a <see
    /// cref="WorkspaceSessionAdapter"/> <c>WindowAdded</c> auto-tile -- the shared-flag invariant
    /// an earlier decision requires and that no prior fact proved through this joint.
    /// </summary>
    [Fact]
    public void BuildPauseGatedSession_SingleHookPauseWrite_BlocksBothChordMatchAndWindowAdded()
    {
        var platform = new FakeKeyboardHookPlatform();
        var channel = Channel.CreateUnbounded<HotkeyAction>();
        using var hook = new LowLevelKeyboardHook(channel.Writer, platform, TimeSpan.FromSeconds(5), () => 0);
        hook.Start();
        // Draining the install-time H+Alt callback FakeKeyboardHookPlatform.Install fires eagerly,
        // so the assertions below observe only activity raised after IsPaused is set.
        channel.Reader.TryRead(out _);

        var workspace = new FakeWorkspace();
        var tree = new LayoutTree();
        var registry = new WindowRegistry();
        var (_, executor) = CompositionRoot.Build(
            new RecordingTilingEngine(), registry, new StaticForegroundWindowSource(IntPtr.Zero),
            new Rect(0, 0, 1920, 1080));
        var exceptionStore = new ExceptionListStore(ExceptionList.Empty);

        using var adapter = CompositionRoot.BuildPauseGatedSession(
            workspace, tree, registry, executor, exceptionStore, hook);

        hook.IsPaused = true;

        platform.RaiseActivity();
        Assert.False(channel.Reader.TryRead(out _));

        var window = new RecordingWindow(new IntPtr(950), Rectangle.FromSize(0, 0, 800, 600));
        workspace.RaiseWindowAdded(window);
        Assert.Null(tree.Root);
    }

    private sealed class RecordingTilingEngine : ITilingEngine
    {
        public int MoveNodeCallCount { get; private set; }
        public Direction LastMoveDirection { get; private set; }
        public int ResizeNodeCallCount { get; private set; }
        public Direction LastResizeDirection { get; private set; }

        public FocusResult NextFocus(Direction direction, LeafNode focused) => FocusResult.NoMatch;

        public bool MoveNode(Direction direction, Node focused)
        {
            MoveNodeCallCount++;
            LastMoveDirection = direction;
            return false;
        }

        public bool ToggleAxis(Node focused) => false;

        public bool ResizeNode(Direction direction, Node focused, double step = LayoutTree.DefaultResizeStep, Func<Node, SplitAxis, (int Min, int Max)>? limitsOf = null)
        {
            ResizeNodeCallCount++;
            LastResizeDirection = direction;
            return false;
        }

        public IReadOnlyList<(WindowRef Window, Rect Bounds)> Arrange(Rect workArea) =>
            Array.Empty<(WindowRef, Rect)>();

        public bool Remove(Node focused) => false;
    }

    private sealed class StaticForegroundWindowSource(nint handle) : IForegroundWindowSource
    {
        public nint GetForegroundHandle() => handle;
    }
}
