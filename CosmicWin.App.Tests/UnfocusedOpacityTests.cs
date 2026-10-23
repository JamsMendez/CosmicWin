using CosmicWin.App.Tests.TestDoubles;
using CosmicWin.Interop;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App.Tests;

/// <summary>
/// While tiling is active, every tiled window except the focused one is drawn at the configured
/// opacity, and every window gets full opacity back the moment it stops being tiled.
/// </summary>
/// <remarks>
/// The same lifecycle as the maximize block (<see cref="MaximizeBlockTests"/>), for the same
/// reason: the change is written onto somebody else's window, so whatever stops CosmicWin managing
/// a window -- removal, minimize, fullscreen, tiling switched off, the adapter disposed -- must hand
/// it back.
/// </remarks>
public sealed class UnfocusedOpacityTests
{
    private static readonly Rectangle Monitor = Rectangle.FromSize(0, 0, 1920, 1080);

    private static readonly Rectangle WorkArea = Rectangle.FromSize(0, 0, 1920, 1032);

    /// <summary>90% of 255, rounded: what <c>unfocused-opacity = 90</c> becomes.</summary>
    private const byte Translucent = 230;

    private sealed class Harness : IDisposable
    {
        public Harness(
            byte unfocusedAlpha = Translucent, WindowArrival arrival = WindowArrival.Created,
            bool previousRunCrashed = false)
        {
            var primary = new FakeDisplay(new IntPtr(1), Monitor, WorkArea, 1.0, true);
            Primary = primary;
            Registry = new WindowRegistry();
            Trees = new TreeManager([primary], primary, Registry);
            Workspace = new FakeWorkspace();
            Adapter = new MultiMonitorWorkspaceAdapter(
                Workspace, Trees, Registry, () => ExceptionList.Empty, () => !TilingOn, () => null)
            {
                UnfocusedAlpha = unfocusedAlpha,
                RepairOpacityOnAdoption = previousRunCrashed,
                OpacityInUseChanged = MarkerChanges.Add,
            };

            First = new RecordingWindow(new IntPtr(10), Rectangle.FromSize(0, 0, 400, 300));
            Second = new RecordingWindow(new IntPtr(20), Rectangle.FromSize(0, 0, 400, 300));
            Workspace.RaiseWindowAdded(First, arrival);
            Workspace.RaiseWindowAdded(Second, arrival);
        }

        public bool TilingOn { get; set; } = true;

        /// <summary>Every report of whether CosmicWin may have windows layered, in order.</summary>
        public List<bool> MarkerChanges { get; } = [];

        public IDisplay Primary { get; }

        public WindowRegistry Registry { get; }

        public TreeManager Trees { get; }

        public FakeWorkspace Workspace { get; }

        public MultiMonitorWorkspaceAdapter Adapter { get; }

        public RecordingWindow First { get; }

        public RecordingWindow Second { get; }

        public bool InTree(RecordingWindow window) =>
            Trees.LeavesOn(Primary).Any(leaf => leaf.Window.Handle == window.Handle);

        public void Dispose() => Adapter.Dispose();
    }

    // ---- drawing ---------------------------------------------------------------------------------

    [Fact]
    public void BelowFullOpacity_EveryAdmittedWindowIsDrawnTranslucent()
    {
        using var h = new Harness();

        Assert.Equal(Translucent, h.First.Opacity);
        Assert.Equal(Translucent, h.Second.Opacity);
    }

    [Fact]
    public void TheFocusedWindow_IsOpaque_AndTheOthersStayTranslucent()
    {
        using var h = new Harness();

        h.Adapter.ApplyOpacity(h.First.Handle);

        Assert.Equal(255, h.First.Opacity);
        Assert.Equal(Translucent, h.Second.Opacity);
    }

    [Fact]
    public void FocusMoving_SwapsWhichWindowIsOpaque()
    {
        using var h = new Harness();
        h.Adapter.ApplyOpacity(h.First.Handle);

        h.Adapter.ApplyOpacity(h.Second.Handle);

        Assert.Equal(Translucent, h.First.Opacity);
        Assert.Equal(255, h.Second.Opacity);
    }

    /// <summary>
    /// Asked on every focus-border refresh, several times a second, so an unchanged answer must
    /// cost no style write at all.
    /// </summary>
    [Fact]
    public void ApplyingTheSameFocusAgain_WritesNothing()
    {
        using var h = new Harness();
        h.Adapter.ApplyOpacity(h.First.Handle);
        var firstWrites = h.First.OpacityRequests.Count;
        var secondWrites = h.Second.OpacityRequests.Count;

        h.Adapter.ApplyOpacity(h.First.Handle);

        Assert.Equal(firstWrites, h.First.OpacityRequests.Count);
        Assert.Equal(secondWrites, h.Second.OpacityRequests.Count);
    }

    /// <summary>A window that refused once is not asked again on every refresh.</summary>
    [Fact]
    public void AWindowThatRefusesTheChange_IsNotAskedAgainOnEveryRefresh()
    {
        using var h = new Harness();
        var refusing = new RecordingWindow(new IntPtr(30), Rectangle.FromSize(0, 0, 400, 300)) { RefuseStyleChanges = true };
        h.Workspace.RaiseWindowAdded(refusing);
        var asked = refusing.OpacityRequests.Count;

        h.Adapter.ApplyOpacity(h.First.Handle);
        h.Adapter.ApplyOpacity(h.Second.Handle);

        Assert.Equal(1, asked);
        Assert.Equal(asked, refusing.OpacityRequests.Count);
        Assert.True(h.InTree(refusing));
    }

    // ---- full opacity (off) ------------------------------------------------------------------------

    /// <summary>At 100% a new window is never touched: the feature is off.</summary>
    [Fact]
    public void AtFullOpacity_ANewWindowIsNeverTouched()
    {
        using var h = new Harness(unfocusedAlpha: 255);

        h.Adapter.ApplyOpacity(h.First.Handle);

        Assert.Empty(h.First.OpacityRequests);
        Assert.Empty(h.Second.OpacityRequests);
    }

    /// <summary>
    /// The user's recovery route: the previous run crashed with windows layered, so the windows it
    /// left translucent are ADOPTED by this start and each one is made fully opaque again.
    /// </summary>
    [Fact]
    public void AtFullOpacity_AfterACrash_AnAdoptedWindowIsMadeOpaque()
    {
        using var h = new Harness(unfocusedAlpha: 255, arrival: WindowArrival.Adopted, previousRunCrashed: true);

        Assert.Equal([null], h.First.OpacityRequests);
        Assert.Equal([null], h.Second.OpacityRequests);
    }

    /// <summary>
    /// After a clean exit there is nothing to repair, and 100 means what it says: no window is
    /// touched, not even one that layers itself with an opacity of its own.
    /// </summary>
    [Fact]
    public void AtFullOpacity_AfterACleanExit_AnAdoptedWindowIsNeverTouched()
    {
        using var h = new Harness(unfocusedAlpha: 255, arrival: WindowArrival.Adopted, previousRunCrashed: false);

        Assert.Empty(h.First.OpacityRequests);
        Assert.Empty(h.Second.OpacityRequests);
    }

    // ---- crash marker ------------------------------------------------------------------------------

    /// <summary>
    /// Reported once when the first window is layered, and once when the last one is given back:
    /// the marker exists exactly while a crash could leave a window translucent.
    /// </summary>
    [Fact]
    public void TheMarker_IsSetWhenTheFirstWindowIsLayered_AndClearedWhenTheLastIsGivenBack()
    {
        using var h = new Harness();

        Assert.Equal([true], h.MarkerChanges);

        h.Adapter.ReleaseOpacity();

        Assert.Equal([true, false], h.MarkerChanges);
    }

    [Fact]
    public void TheMarker_StaysWhileAnyLayeredWindowRemains()
    {
        using var h = new Harness();

        h.Workspace.RaiseWindowRemoved(h.First);

        Assert.Equal([true], h.MarkerChanges);

        h.Workspace.RaiseWindowRemoved(h.Second);

        Assert.Equal([true, false], h.MarkerChanges);
    }

    /// <summary>
    /// Cleared only once the last window HAS its opacity back: killed in between, the next start
    /// must still find the marker.
    /// </summary>
    [Fact]
    public void TheMarker_IsClearedOnlyAfterTheLastGiveBack()
    {
        using var h = new Harness();
        (byte First, byte Second)? atClear = null;
        h.Adapter.OpacityInUseChanged = inUse =>
        {
            if (!inUse)
            {
                atClear = (h.First.Opacity, h.Second.Opacity);
            }
        };

        h.Adapter.ReleaseOpacity();

        Assert.Equal(((byte)255, (byte)255), atClear);
    }

    /// <summary>
    /// A give-back that did not land leaves a window translucent, so the marker stays -- through
    /// the release and through a clean exit -- and the next start repairs it.
    /// </summary>
    [Fact]
    public void AFailedGiveBack_KeepsTheMarker_EvenThroughACleanExit()
    {
        var h = new Harness();
        h.Second.RefuseStyleChanges = true;

        h.Adapter.ReleaseOpacity();
        h.Dispose();

        Assert.Equal([true], h.MarkerChanges);
    }

    /// <summary>
    /// A window already translucent that refuses a NEW alpha still carries the old one, so it is
    /// given back before it is forgotten rather than left translucent for good.
    /// </summary>
    [Fact]
    public void ARefusedRewrite_OfAWindowAlreadyTranslucent_GivesItsOpacityBack()
    {
        using var h = new Harness();
        h.First.RefuseOpacityWrites = true;

        h.Adapter.ApplyOpacity(h.First.Handle);

        Assert.Equal(255, h.First.Opacity);
        Assert.Null(h.First.OpacityRequests[^1]);
    }

    /// <summary>
    /// A clean exit always clears it -- also when nothing was layered this run, which is how a
    /// marker left by a crash goes away once its repair has run.
    /// </summary>
    [Fact]
    public void ACleanExit_ClearsTheMarker_EvenWithNothingLayered()
    {
        var h = new Harness(unfocusedAlpha: 255, arrival: WindowArrival.Adopted, previousRunCrashed: true);

        h.Dispose();

        Assert.Equal([false], h.MarkerChanges);
    }

    // ---- giving it back ----------------------------------------------------------------------------

    [Fact]
    public void ARemovedWindow_IsMadeOpaqueAgain()
    {
        using var h = new Harness();

        h.Workspace.RaiseWindowRemoved(h.First);

        Assert.Equal(255, h.First.Opacity);
        Assert.Null(h.First.OpacityRequests[^1]);
    }

    [Fact]
    public void AMinimizedWindow_IsMadeOpaque_AndTranslucentAgainWhenRestored()
    {
        using var h = new Harness();

        h.First.SimulateMinimize();
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.False(h.InTree(h.First));
        Assert.Equal(255, h.First.Opacity);

        h.First.SimulateRestore(Rectangle.FromSize(100, 100, 400, 300));
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(h.InTree(h.First));
        Assert.Equal(Translucent, h.First.Opacity);
    }

    /// <summary>A fullscreen video or game is drawn opaque, and translucent again once it leaves.</summary>
    [Fact]
    public void AWindowThatGoesFullscreen_IsOpaque_AndTranslucentAgainWhenItLeaves()
    {
        using var h = new Harness();

        h.First.SimulateFullscreen(Monitor);
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.Equal(255, h.First.Opacity);

        h.First.SimulateLeaveFullscreen(Rectangle.FromSize(100, 100, 400, 300));
        h.Workspace.RaiseWindowBoundsChanged(h.First);

        Assert.True(h.InTree(h.First));
        Assert.Equal(Translucent, h.First.Opacity);
    }

    /// <summary>Tiling switched off: every window is opaque again. Switched back on, the opacity returns.</summary>
    [Fact]
    public void ReleasingTheOpacity_MakesEveryWindowOpaque_AndApplyingItAgainRestoresIt()
    {
        using var h = new Harness();
        h.Adapter.ApplyOpacity(h.First.Handle);

        h.Adapter.ReleaseOpacity();

        Assert.Equal(255, h.First.Opacity);
        Assert.Equal(255, h.Second.Opacity);
        Assert.Null(h.Second.OpacityRequests[^1]);

        h.Adapter.ApplyOpacity(h.First.Handle);

        Assert.Equal(255, h.First.Opacity);
        Assert.Equal(Translucent, h.Second.Opacity);
    }

    [Fact]
    public void ReleasingTheOpacityTwice_AsksEachWindowOnlyOnce()
    {
        using var h = new Harness();

        h.Adapter.ReleaseOpacity();
        var asked = h.Second.OpacityRequests.Count;
        h.Adapter.ReleaseOpacity();

        Assert.Equal(asked, h.Second.OpacityRequests.Count);
    }

    [Fact]
    public void DisposingTheAdapter_MakesEveryWindowOpaque()
    {
        var h = new Harness();

        h.Dispose();

        Assert.Equal(255, h.First.Opacity);
        Assert.Equal(255, h.Second.Opacity);
    }
}
