using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using CosmicWin.App.Alerts;
using CosmicWin.App.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CosmicWin.App.Input;
using CosmicWin.App.Startup;
using CosmicWin.App.Tray;
using CosmicWin.App.Wallpaper;
using CosmicWin.Interop;
using CosmicWin.Interop.Win32;
using CosmicWin.Interop.Win32.VirtualDesktops;
using CosmicWin.Layout;
using CosmicWin.Layout.Filters;

namespace CosmicWin.App;

/// <summary>
/// Extracts <c>App.xaml.cs</c>'s
/// composition into a plain, directly-testable class. Four consecutive closures defended the
/// composition site from OUTSIDE a WPF <see
/// cref="System.Windows.Application"/> subclass -- three by tightening the type system, the last
/// (<c>CompositionSiteArchitectureTests</c>, now deleted) by reading its source text -- and each
/// closure fell to a mutation the previous one had not anticipated. This class does not add a
/// fifth outside-in inspection: it moves the composition logic itself somewhere a real test can
/// reach it. <see cref="Wire"/> is a plain static method with every collaborator supplied by the
/// caller (mirroring the existing <see cref="CompositionRoot.Build"/>/<see
/// cref="CompositionRoot.BuildPauseGatedSession"/> idiom), so <c>AppCompositionTests</c> drives it
/// end to end with a real <see cref="LowLevelKeyboardHook"/> (via a fake <see
/// cref="IKeyboardHookPlatform"/>, no live desktop needed) and a real <see
/// cref="TrayMenuController"/>, asserting actual pause-gate BEHAVIOR rather than call-site spelling.
/// <see cref="WireProduction"/> is the sole place that supplies the real Win32 collaborators; <see
/// cref="App.xaml.cs"/> now does nothing but call it and dispose the result -- see
/// <c>AppEntryPointThinnessTests</c> for the guard that keeps it that way.
/// </summary>
public sealed class AppComposition : IDisposable
{
    private readonly ActionDispatcher _dispatcher;
    private readonly LowLevelKeyboardHook _hook;
    private readonly IWorkspace _workspace;
    private readonly MultiMonitorWorkspaceAdapter _sessionAdapter;
    private readonly IDisposable _tray;
    private readonly IDisposable _reconcile;

    /// <summary>Both null when no shown-window watcher was supplied -- dialogs are then simply left where they open.</summary>
    private readonly IFocusBorder? _focusBorder;
    private readonly Action _unfollowFocusedWindow;
    private readonly IWindowShownWatcher? _windowShown;
    private readonly FloatingDialogAdapter? _dialogAdapter;

    /// <summary>Both null unless a video wallpaper host/player were supplied -- see <see cref="Wire"/>'s startup activation and the tray's <c>setVideoWallpaperPath</c> hook.</summary>
    private readonly IVideoWallpaperHost? _videoWallpaperHost;
    private readonly IVideoWallpaperPlayer? _videoWallpaperPlayer;
    private readonly Action _disposeVideoWallpaper;

    /// <summary>
    /// F5 (video-wallpaper-review-followups, <c>R3-keepalive-flag-cross-thread</c>): a plain
    /// <c>bool</c> read on one thread and written on another is not guaranteed to observe the
    /// latest write -- the CLR permits caching a field's value in a register across loop
    /// iterations/method calls absent a memory barrier. <c>videoWallpaperActive</c> and
    /// <c>videoWallpaperKeepAlivePending</c> in <see cref="Wire"/> are exactly that: written from
    /// the video-wallpaper thread, read from the UI thread's watch tick (or vice versa for the
    /// pending flag). Local variables captured by a closure cannot be marked <c>volatile</c> --
    /// C# only allows that on a field -- so this tiny holder gives each flag a real field behind
    /// <see cref="System.Threading.Volatile"/>, while staying a plain boolean at every call site.
    /// <see cref="System.Threading.Interlocked"/> is not needed here: every access is a bare
    /// read or a bare write, never a read-modify-write that has to be atomic as one step.
    /// </summary>
    private sealed class VolatileFlag
    {
        private int _value;

        public bool Value
        {
            get => Volatile.Read(ref _value) != 0;
            set => Volatile.Write(ref _value, value ? 1 : 0);
        }
    }

    private AppComposition(
        ActionDispatcher dispatcher, LowLevelKeyboardHook hook, IWorkspace workspace,
        MultiMonitorWorkspaceAdapter sessionAdapter, IDisposable tray, IDisposable reconcile,
        IWindowShownWatcher? windowShown, FloatingDialogAdapter? dialogAdapter,
        IFocusBorder? focusBorder, IVideoWallpaperHost? videoWallpaperHost,
        IVideoWallpaperPlayer? videoWallpaperPlayer, Action disposeVideoWallpaper,
        Action unfollowFocusedWindow)
    {
        _dispatcher = dispatcher;
        _hook = hook;
        _workspace = workspace;
        _sessionAdapter = sessionAdapter;
        _tray = tray;
        _reconcile = reconcile;
        _windowShown = windowShown;
        _dialogAdapter = dialogAdapter;
        _focusBorder = focusBorder;
        _videoWallpaperHost = videoWallpaperHost;
        _videoWallpaperPlayer = videoWallpaperPlayer;
        _disposeVideoWallpaper = disposeVideoWallpaper;
        _unfollowFocusedWindow = unfollowFocusedWindow;
    }

    /// <summary>
    /// How often the cheap checks run. Everything on this tick must stay cheap, because this is the
    /// responsiveness floor for anything CosmicWin can only notice by ASKING -- a desktop closing
    /// and handing its windows away raises no event we subscribe to.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How often the FULL reconciliation runs, expressed in watch ticks. <c>Poll</c> enumerates
    /// every top-level window on the system, so it stays at its original two seconds while the
    /// desktop check -- a handful of lookups over tracked windows -- runs five times as often.
    /// Measured as the cause of a one-second lag before windows from a closed desktop were tiled:
    /// the work was cheap, the wait was not.
    /// <para>
    /// WT-1's "polling fallback reconciliation pass on a bounded interval" is this slower tick.
    /// Frequent enough that a window the hook dropped is picked up before the user notices, cheap
    /// enough to ignore: a pass only raises events for windows whose bounds or membership ACTUALLY
    /// changed, so a steady desktop costs one enumeration and nothing else.
    /// </para>
    /// </summary>
    internal const int PollEveryNthWatch = 5;

    /// <summary>
    /// Wires every collaborator, in <c>App.OnStartup</c>'s exact order: <see
    /// cref="CompositionRoot.Build"/> against <paramref name="treeManager"/>'s <see
    /// cref="TreeManager.Primary"/> tree, then <paramref name="treeManager"/> and <paramref
    /// name="focusTrace"/> are ALSO assigned onto
    /// the returned executor (closes: a hotkey mutation on a secondary monitor's
    /// focused window now arranges that SAME secondary tree, not always the primary one; <paramref
    /// name="focusTrace"/> is mandatory rather than optional so the MR-2 diagnostic cannot be
    /// silently dropped from the one composition site that has to carry it), then the hook, then <see cref="MultiMonitorWorkspaceAdapter"/> --
    /// 's real production caller of <paramref name="treeManager"/>
    /// -- then <paramref name="workspace"/>.Open(), <paramref name="hook"/>.Start(), the tray, then
    /// the dispatcher loop.
    /// </summary>
    public static AppComposition Wire(
        IWorkspace workspace,
        TreeManager treeManager,
        WindowRegistry registry,
        IForegroundWindowSource foreground,
        ExceptionListStore exceptionStore,
        IFocusTrace focusTrace,
        Action disableTaskTrigger,
        Func<TimeSpan, Action, IDisposable> scheduleReconcile,
        Func<ChannelWriter<HotkeyAction>, LowLevelKeyboardHook> hookFactory,
        Func<ExceptionList> loadExceptions,
        Action shutdown,
        Func<TrayMenuController, IDisposable> buildTray,
        // Required, unlike the persistX delegates below: there is no sensible default for "copy
        // this file somewhere and hand back where it landed", and this is the seam a test
        // substitutes to avoid touching real disk. Production wires it to
        // VideoWallpaperImport.Import. Must stay ahead of every optional parameter below --
        // a required parameter cannot follow one with a default in C#.
        Func<string, string> importVideoWallpaper,
        IVirtualDesktopService? virtualDesktops = null,
        Diagnostics.IDesktopTrace? desktopTrace = null,
        Func<nint, Guid>? resolveWindowDesktop = null,
        IWindowShownWatcher? windowShown = null,
        IFocusBorder? focusBorder = null,
        Action<Action>? scheduleOnOwningThread = null,
        bool focusBorderEnabled = true,
        Action<bool>? persistFocusBorder = null,
        uint? focusBorderColor = null,
        Action<uint?>? persistBorderColor = null,
        bool tilingEnabled = true,
        Action<bool>? persistTiling = null,
        // T5 (alert-tile-mosaic): mirrors loadExceptions -- a fresh read of settings.conf's `gap`
        // key, invoked from ReloadGap (below) on the SAME WE-3 "Reload" trigger the exception list
        // already uses. Unset -- as in every test that predates this parameter -- Reload never
        // touches TreeArranger.Gap, exactly the exceptions-only behaviour this project has always had.
        Func<int>? loadGap = null,
        // Optional, same as the persistX delegates above.
        Action<string>? persistVideoWallpaperPath = null,
        // noop-followups, F1: reads a point-in-time VideoFileSnapshot (identity + size + last-write)
        // of the path it is given, or null on any failure. Production wires
        // VideoWallpaperImport.TryReadSnapshot; null (the default in every test that predates this
        // parameter, and every test that predated same-video-noop's isSameVideoFile before it)
        // means "never take a snapshot", so the http path always reloads exactly as it did before
        // either parameter existed. SwitchVideoWallpaper never calls this directly -- always through
        // the SafeReadSnapshot wrapper below, which also catches an exception from an INJECTED
        // reader (a real GetFileInformationByHandle-backed one never throws, but a test double is
        // free to) and treats it the same way: as "no snapshot".
        Func<string, VideoWallpaperImport.VideoFileSnapshot?>? readVideoFileSnapshot = null,
        // The T3/T4 collaborators. Both null (the default in every test that predates T6) means
        // "nothing to attach or play" -- construction alone never attaches or plays anything, only
        // TryAttach/TryPlay do, and those only run when a path is ALSO present (see below).
        IVideoWallpaperHost? videoWallpaperHost = null,
        IVideoWallpaperPlayer? videoWallpaperPlayer = null,
        Action<Action>? scheduleVideoWallpaperWork = null,
        Action? disposeVideoWallpaper = null,
        bool alertsEnabled = false,
        Func<string, Func<string, string>, Action<string>?, IAlertCommandServer>? createAlertCommandServer = null,
        // The ONE switch for the local HTTP server (settings key http-server). When on, every route
        // is in the routing table: alerts (which additionally needs alertsEnabled -- there is no
        // alert queue to answer through otherwise), video and scene, each keeping its own mode
        // guard and 503 behaviour. Default off: no port opened, token file never touched.
        // createLocalHttpCommandServer mirrors createAlertCommandServer's seam, with trailing
        // handleVideoWallpaperSwitch and handleWallpaperSceneSwitch delegates; loadAlertHttpToken
        // mirrors it for AlertHttpTokenFile.LoadOrCreate, so a wiring test never binds a real port
        // or touches %LOCALAPPDATA%.
        bool httpServerEnabled = false,
        int httpServerPort = AlertHttpProtocol.DefaultPort,
        Func<int, string, Func<string, string>?, Action<string>?, Func<string, bool>?, Func<string, bool>?, IAlertCommandServer>? createLocalHttpCommandServer = null,
        Func<string?>? loadAlertHttpToken = null,
        Func<bool>? alertDesktopVisible = null,
        // T10 (live-alert-wallpaper): the real production signal for "something is covering the
        // primary monitor right now" (a fullscreen video, browser tab, etc.) -- see
        // PrimaryMonitorFullscreenDetector.IsPrimaryMonitorCoveredByFullscreenWindow, wired by
        // WireProduction. Kept separate from alertDesktopVisible, which stays the full override
        // escape hatch a test uses to bypass this composition entirely: unset (every test that
        // predates T10, and any caller that overrides alertDesktopVisible directly) reads as "never
        // covered", exactly the T8 behaviour this task is fixing.
        Func<bool>? isPrimaryMonitorCovered = null,
        // Already-resolved from Settings before Wire is called, same as focusBorderColor/
        // tilingEnabled above -- not re-read from disk in here.
        string? videoWallpaperPath = null,
        // D3 (html-wallpaper-demo): both modes are real, supported behaviour. Video: startup attaches
        // AND plays videoWallpaperPath exactly as before this parameter existed. Html (CosmicWin's
        // default renderer since S8, wallpaper-scene-http-endpoint): startup attaches the SAME host
        // with no player involved at all (see AttachHtmlWallpaper below), regardless of whether a path
        // happens to be configured, because an animated HTML scene page is the wallpaper instead. See
        // odd/tasks/html-wallpaper-demo.md.
        //
        // This default stays Video: it is a TEST SEAM, not the product default (production always
        // passes wallpaperMode: settings.WallpaperMode from WireProduction, whose own default is now
        // Html -- see Settings.WallpaperMode). Every video-playback wiring test in
        // AppCompositionTests/VideoWallpaperPlaybackWiringTests calls Wire() without naming
        // wallpaperMode at all, relying on this parameter default to stay Video; flipping it to Html
        // would silently turn every one of those into an html-mode test instead of what it actually
        // exercises.
        WallpaperMode wallpaperMode = WallpaperMode.Video,
        // The abstraction Wire operates on for the concrete WebViewAlertLayerController.SwitchScene
        // method (mirroring startAlertLayer/endAlertLayer/preloadAlertLayer above, which do the same
        // for Start/End/Preload) -- returns whether the switch was accepted for dispatch, exactly
        // like handleVideoWallpaperSwitch's own contract. Unset (every test that predates S4, and
        // production when no alert layer exists) means the composition has no live scene switch to
        // offer at all: the HTTP handler answers false (503) without dispatching anything.
        Func<WallpaperScene, bool>? switchHtmlWallpaperScene = null,
        // Mirrors persistVideoWallpaperPath above, for the SAME reason: an optional seam so a test
        // never touches real disk. Production (WireProduction) saves the scene into settings.conf,
        // exactly like the video route persists its own path.
        Action<WallpaperScene>? persistWallpaperScene = null,
        // The desktop's windows, TOPMOST FIRST -- what ActionExecutor.ResolveFloatingWindows needs
        // to answer an untiled focus chord's stack pass. A delegate rather than a new IWorkspace
        // member: IWorkspace.Snapshot is dictionary-insertion order, not z-order, and every
        // implementation and every test double of that interface would have to grow a second
        // ordering guarantee to carry ONE optional composition-site fact that only this one caller
        // needs. Unset -- as in every test that predates it -- ResolveFloatingWindows stays unset
        // too, so the behaviour is exactly what it is today.
        Func<IReadOnlyList<nint>>? zOrder = null,
        // Re-reads the monitors and returns the ones whose geometry changed since the last call,
        // each reported ONCE. A delegate for the reason zOrder is: the only real answer is
        // Win32DisplayManager.Refresh, and the Wire tests supply their own. Unset -- as in every
        // test that predates it -- the work area stays what it was at startup, exactly as before.
        Func<IReadOnlyList<IDisplay>>? refreshDisplays = null,
        Action<AlertShowRequest>? startAlertLayer = null,
        Action? endAlertLayer = null,
        Action<TimeSpan>? shakeAlertVideo = null,
        IDisposable? alertLayer = null,
        Func<bool>? alertRendererReady = null,
        // T9c (webview-alert-layer): begins keeping ONE WebView2 controller alive for the process's
        // life (feature doc, "Idle cost (superseded 2026-09-24)") instead of the old create/dispose
        // per alert. Called once, on the owning UI thread once it is pumping, when alerts are
        // enabled -- never gates alertRendererReady, which stays the host's own composition
        // readiness; a Start before the layer is ready is held as a pending show instead (see
        // WebViewAlertLayerController.Start/AlertLayerPreloadState).
        Action? preloadAlertLayer = null,
        // Where the alert queue's own time reads (Enqueue/Advance and the remaining-duration
        // check below) come from. Unset -- as every test predating this parameter, and production
        // via WireProduction -- reads the real system clock. Tests inject a manual TimeProvider so
        // the queue's second-scale deadlines advance deterministically instead of via Thread.Sleep.
        TimeProvider? timeProvider = null,
        // T4 (mini-scene-window): the corner window that replaces the wallpaper in
        // WallpaperMode.HtmlMini. Wire never builds it (production builds it on the UI STA in
        // WireProduction, tests pass a fake); it is only ever touched in mini mode, on the owning
        // thread. Null in mini mode means "no window": nothing shows, the scene route answers 503.
        IMiniSceneWindow? miniWindow = null,
        WallpaperScene wallpaperScene = WallpaperScene.Processing,
        int wallpaperFps = 60,
        // The corner the window starts in, and the persist seam Alt+M uses (T5). Already resolved
        // from Settings before Wire is called, like every other value above.
        MiniPosition miniPosition = MiniPosition.TopRight,
        Action<MiniPosition>? persistMiniPosition = null)
    {
        var alertClock = timeProvider ?? TimeProvider.System;
        // The live answer to "is CosmicWin laying windows out", owned here for the same reason the
        // border flag below is: the tray item, the executor's chord gate and both window adapters
        // all have to read ONE decision, and whichever of them kept its own copy would become a
        // second owner of it.
        var tiling = tilingEnabled;

        // Displays whose work area changed and whose layout has not been redone yet. Kept apart from
        // the refresh because the display reports a change ONCE: a change that arrives while the
        // layout is frozen (paused, or tiling off) has to wait here, or it is gone for good.
        var displaysAwaitingReflow = new List<IDisplay>();

        // The live answer to "is the border on", owned here because BOTH the tray item and
        // UpdateFocusBorder need it and neither may become the other's source of truth.
        var borderEnabled = focusBorderEnabled;

        // Same shape, same reason. The overlay knows what it is painting but cannot be asked, and
        // the tray has to seed its picker with what is on screen right now.
        var borderColor = focusBorderColor;
        var primary = treeManager.Primary;
        treeManager.TryGetTree(primary, out var primaryTree);
        var workArea = WorkAreaResolver.Resolve(primary);

        // A chord that throws no longer kills the pump; this is where it says so. Without a sink
        // the manager would drop the chord in silence and look perfectly healthy doing it.
        var (dispatcher, executor) = CompositionRoot.Build(
            primaryTree!, registry, foreground, workArea,
            onActionFailed: (action, error) => desktopTrace?.Record(
                $"action-failed {action.Kind} arg={action.Argument} " +
                $"{error.GetType().Name}: {error.Message}"));
        executor.TreeManager = treeManager;
        executor.FocusTrace = focusTrace;
        executor.VirtualDesktops = virtualDesktops;
        executor.DesktopTrace = desktopTrace;
        executor.TilingEnabled = () => tiling;

        // The dimension is inert until something answers these. Left unset -- as every test does --
        // every tree is filed under Guid.Empty and the model behaves exactly as it did before.
        if (virtualDesktops is not null)
        {
            treeManager.CurrentDesktop = () => virtualDesktops.CurrentDesktopId;
        }
        var hook = hookFactory(dispatcher.Writer);

        // Which desktop the USER is on, and where it sits. Kept here rather than asked of the shell
        // on demand, because the one moment it is needed -- a window arriving -- is the one moment
        // the shell's answer is wrong: Windows can have followed the new window before CosmicWin
        // hears about it, so asking then reports where the window took the user.
        //
        // Refreshed from two places, and BOTH are required. The reconciliation tick catches a switch
        // CosmicWin did not make (Win+Ctrl+arrow, Task View); the switch chord refreshes it at once,
        // because leaving that to the tick would leave the answer a full interval stale and send a
        // window opened straight after Alt+2 back to where the user just left.
        var lastDesktop = virtualDesktops?.CurrentDesktopId ?? Guid.Empty;
        var lastDesktopIndex = virtualDesktops?.CurrentIndex ?? 0;

        // The dispatcher runs chords on a pool thread; the overlay is a WPF window and must be
        // touched on the thread that owns it, which is the same one the reconciliation timer already
        // runs on. Unset -- as every test does -- it runs inline, which is what a test on one thread
        // wants. Declared HERE, above the first collaborator that has to reach the border, rather
        // than beside its other use further down.
        var onOwningThread = scheduleOnOwningThread ?? (work => work());
        var onVideoWallpaperThread = scheduleVideoWallpaperWork ?? onOwningThread;
        var disposeVideoWallpaperBase = disposeVideoWallpaper ?? (() =>
        {
            videoWallpaperPlayer?.Dispose();
            videoWallpaperHost?.Dispose();
        });

        var alertQueueLock = new object();
        var alertQueue = alertsEnabled ? new AlertQueue(onDiagnostic: message => desktopTrace?.Record(message)) : null;
        IAlertCommandServer? alertServer = null;
        IAlertCommandServer? httpAlertServer = null;
        ActiveAlert? displayedAlert = null;
        ActiveAlert? shakenAlert = null;

        // What the border was last told to do. Three focus-border defects have been fixed so far and
        // every one of them was verified by a unit fact or by eye, never by a timestamp -- and the
        // whole defect class is "the border is late, or on the wrong rectangle", measured inside the
        // 400ms reconciliation interval, right at the edge of what an eye reliably catches.
        //
        // Recorded on CHANGE only, deliberately. UpdateFocusBorder runs on every tick, so tracing
        // each call would write a line every 400ms for the life of the session and bury the four
        // lines that matter. A transition is the whole signal: when the border let go, and what it
        // moved to.
        var lastBorderDecision = string.Empty;

        // The rectangle the framed window was last seen PASSING THROUGH, while a hand drag or
        // resize is still in flight, and the handle it belongs to.
        //
        // It exists because the live report is an event and the tick is not. Measured on hardware
        // the moment the live follow started working: over one three-second resize the border
        // reached the new width fifteen times and snapped back to the pre-drag width five times --
        // once per reconciliation interval, because the tick reads the only rectangle it has, the
        // window's own, and that one is a gesture behind ON PURPOSE. Two answers, alternating, for
        // the length of the drag.
        //
        // So the live rectangle outlives the event that carried it, until the gesture ends. It is a
        // stand-in for a cache that is deliberately behind, and it is dropped the moment that cache
        // catches up -- otherwise it would be the same staleness pointing the other way.
        nint gestureHandle = 0;
        var gestureBounds = default(Rectangle);

        /// <summary>Where <paramref name="handle"/> is mid-gesture, or null if it is not in one.</summary>
        Rectangle? GestureBoundsFor(nint handle) =>
            gestureHandle != 0 && gestureHandle == handle ? gestureBounds : null;

        /// <summary>
        /// The gesture on <paramref name="handle"/> is over -- it was dropped, or the window it was
        /// measured on is gone. Windows reuses handles, so a rectangle left behind here would land
        /// on whatever takes this one next.
        /// </summary>
        void ForgetGesture(nint handle)
        {
            if (gestureHandle == handle)
            {
                gestureHandle = 0;
            }
        }

        void RecordBorderDecision(string decision)
        {
            if (decision == lastBorderDecision)
            {
                return;
            }

            lastBorderDecision = decision;
            desktopTrace?.Record($"border {decision}");
        }

        // The path a successful (re)pick last activated, so a LATER pick that fails on import knows
        // what to fall back to -- the constraint that picking a video must never leave the
        // wallpaper dead. Starts at whatever was already configured before this composition ran
        // (null on a machine that has never picked one), the same value the startup activation
        // below reads, and is advanced only after setVideoWallpaperPath's import+persist below both
        // succeed -- never merely attempted.
        var currentVideoWallpaperPath = videoWallpaperPath;

        // T3 (video-wallpaper-repick-and-slideshow): whether a video is genuinely playing right
        // now -- host attached AND the player actually started, not merely "collaborators wired".
        // The 400ms watch tick below reads this to decide whether the keep-alive TryAttach below
        // is worth posting at all; an unconfigured wallpaper, or one whose last activation failed,
        // must cost the tick nothing.
        // F5: a VolatileFlag, not a plain bool -- written on the video-wallpaper thread, read on
        // the UI thread's watch tick.
        var videoWallpaperActive = new VolatileFlag();

        // D3 (html-wallpaper-demo): the html-mode equivalent of videoWallpaperActive above -- true
        // once AttachHtmlWallpaper's TryAttach has actually succeeded, false otherwise. Never touched
        // by ActivateVideoWallpaper/SwitchVideoWallpaper, exactly as videoWallpaperActive is never
        // touched by AttachHtmlWallpaper: wallpaperMode is chosen once, at Wire time, and the two
        // flags describe two mutually exclusive modes that are never mixed within one running process.
        //
        // S7 (wallpaper-scene-http-endpoint, R3-html-mode-video-switch-not-guarded): before S7 this
        // last claim was aspirational only for the SWITCH side -- wallpaperMode being chosen once
        // says nothing about SwitchVideoWallpaper, which had no mode check of its own, so a tray
        // pick or an HTTP video switch reaching this composition in html mode WOULD have set
        // videoWallpaperActive right here too, alongside htmlWallpaperActive from startup. It is
        // SwitchVideoWallpaper's own html-mode guard (see its remarks) that now makes "never mixed"
        // true in practice, not merely wallpaperMode being read once.
        var htmlWallpaperActive = new VolatileFlag();

        // T4 (mini-scene-window): mini mode replaces the wallpaper entirely -- no host, no player,
        // no wallpaper alert layer -- so it has its own "is it up" signal, true once the window's
        // Show succeeded. Like the two flags above it is chosen once at Wire time, never mixed.
        var miniMode = wallpaperMode == WallpaperMode.HtmlMini;
        var miniWindowActive = new VolatileFlag();
        var currentMiniPosition = miniPosition;

        // The mini window's rectangle for the PRIMARY display right now. Read live, at the moment of
        // placement, because the taskbar can move or auto-hide after wiring (the display object
        // updates itself in place on every watch tick's refreshDisplays call).
        Rect MiniPlacement(MiniPosition corner)
        {
            var primary = treeManager.Primary;
            return MiniWindowPlacement.Compute(
                WorkAreaResolver.Resolve(primary), primary.Bounds.Height, corner);
        }

        // Runs on the owning UI thread (the window's owner). One attempt, no retry loop: a failed
        // Show is traced and the app carries on without the window.
        void StartMiniWindow()
        {
            if (miniWindow is null)
            {
                return;
            }

            try
            {
                var shown = miniWindow.Show(wallpaperScene, wallpaperFps, MiniPlacement(currentMiniPosition));
                miniWindowActive.Value = shown;
                desktopTrace?.Record($"mini-window phase=startup shown={shown}");
            }
            catch (Exception error) when (IsRecoverableFailure(error))
            {
                desktopTrace?.Record($"mini-window phase=startup show-failed error={error.GetType().Name}");
            }
        }

        void ReplaceMiniWindow()
        {
            if (miniWindow is null || !miniWindowActive.Value)
            {
                return;
            }

            try
            {
                miniWindow.MoveTo(MiniPlacement(currentMiniPosition));
            }
            catch (Exception error) when (IsRecoverableFailure(error))
            {
                desktopTrace?.Record($"mini-window move-failed error={error.GetType().Name}");
            }
        }

        // The alert queue's renderer seam: the wallpaper alert layer's Start/End normally, the mini
        // window's ShowAlert/HideAlert in mini mode. Both take the same AlertShowRequest. The mini
        // window's members belong to its owning UI thread (they throw elsewhere), and the queue's
        // callers are not all provably on it -- so both are always posted there, in order.
        Action<AlertShowRequest>? alertStart = miniMode
            ? (miniWindow is null ? null : request => onOwningThread(() => RunMiniAlert(() => miniWindow.ShowAlert(request), "show")))
            : startAlertLayer;
        Action? alertEnd = miniMode
            ? (miniWindow is null ? null : () => onOwningThread(() => RunMiniAlert(miniWindow.HideAlert, "hide")))
            : endAlertLayer;

        // Posted work cannot report back to the queue, so a failure is traced here instead of escaping
        // into the dispatcher.
        void RunMiniAlert(Action work, string what)
        {
            try
            {
                work();
            }
            catch (Exception error) when (IsRecoverableFailure(error))
            {
                desktopTrace?.Record($"mini-window alert-{what}-failed error={error.GetType().Name}");
            }
        }

        // Set the moment a keep-alive TryAttach is posted, cleared the moment it actually runs --
        // never both true at once for longer than one video-wallpaper work item. Without this a
        // video-wallpaper thread slower than 400ms would see its queue grow one item per tick
        // instead of the single standing keep-alive the task calls for. F5: also a VolatileFlag --
        // set on the UI thread, cleared on the video-wallpaper thread.
        var videoWallpaperKeepAlivePending = new VolatileFlag();

        // noop-followups, F1: the SNAPSHOT of the played file taken the moment playback last
        // actually started -- read here, on the video wallpaper thread, never re-read at compare
        // time. Re-reading the imported destination fresh at compare time cannot detect an
        // in-place edit (see VideoFileSnapshot's remarks: a hard-linked destination and its source
        // are literally the same file, so a fresh read of either always agrees with a fresh read
        // of the other, edited or not); only a comparison against a STALE snapshot taken before
        // the edit can. Cleared to null whenever videoWallpaperActive itself is cleared, so the two
        // always agree: no snapshot is ever kept around for a video that is not actually playing.
        VideoWallpaperImport.VideoFileSnapshot? currentVideoSnapshot = null;

        // noop-followups, F1 (R3-predicate-throw-not-contained): every call to the INJECTED
        // readVideoFileSnapshot delegate goes through here, never direct -- a test double is free
        // to throw where the real, production TryReadSnapshot never does, and a throw from either
        // must read as "no snapshot" (same as a null result) rather than escape the video
        // wallpaper work item and take the whole switch down with it.
        VideoWallpaperImport.VideoFileSnapshot? SafeReadSnapshot(string path)
        {
            try
            {
                return readVideoFileSnapshot?.Invoke(path);
            }
            catch
            {
                return null;
            }
        }

        void ActivateVideoWallpaper(string phase, string path)
        {
            if (videoWallpaperHost is null || videoWallpaperPlayer is null)
            {
                return;
            }

            var pathExists = File.Exists(path);

            // F1: snapshot the path about to (re)start -- the IMPORTED destination on every
            // caller (startup's configured path, a restore's previous import, or a fresh
            // switch's own import result), never the raw source a caller passed to
            // SwitchVideoWallpaper. Read BEFORE TryPlay opens the file (R3-snapshot-after-play-window):
            // an in-place edit landing while the player opens it then differs from this baseline
            // and the next repeat request reloads, instead of becoming the baseline and being
            // skipped as unchanged. Kept only when playback actually comes up, matching
            // videoWallpaperActive itself.
            var snapshotBeforePlay = SafeReadSnapshot(path);
            var attached = videoWallpaperHost.TryAttach();
            bool? played = null;
            if (attached)
            {
                played = videoWallpaperPlayer.TryPlay(videoWallpaperHost, path);
            }

            videoWallpaperActive.Value = attached && played == true;
            currentVideoSnapshot = videoWallpaperActive.Value ? snapshotBeforePlay : null;

            desktopTrace?.Record(
                $"video-wallpaper phase={phase} pathExists={pathExists} " +
                $"tryAttach={attached} tryPlay={(played is { } result ? result.ToString() : "skipped")}");
        }

        /// <summary>
        /// D3 (html-wallpaper-demo): the html-mode startup activation -- attaches the SAME
        /// <paramref name="videoWallpaperHost"/> a video path would use, with NO player involved at
        /// all: D1 proved TryAttach alone builds the composition swapchain (D3D device + one
        /// black test-pattern Present), which is all the WebView2 overlay above it needs to render
        /// over. Never calls TryPlay, even if a video path happens to be configured -- html mode
        /// always wins over a stale video-wallpaper-path setting. videoWallpaperPlayer's own Shake()
        /// becomes an unreachable no-op with no active playback session (D1); left as-is on purpose,
        /// the scene page shakes itself instead.
        /// </summary>
        void AttachHtmlWallpaper(string phase)
        {
            if (videoWallpaperHost is null)
            {
                return;
            }

            var attached = videoWallpaperHost.TryAttach();
            htmlWallpaperActive.Value = attached;
            desktopTrace?.Record($"video-wallpaper phase={phase} mode=html attached={attached}");
        }

        /// <summary>
        /// The "stop, import, persist, (re)activate" sequence <c>setVideoWallpaperPath</c> below
        /// used to run as an inline closure, now a named operation next to
        /// <see cref="ActivateVideoWallpaper"/> so a second caller -- the HTTP video-wallpaper
        /// endpoint (V4) -- can trigger the exact same switch without duplicating it (the V4
        /// constraint: imports must stay serialized on this one path, never a second caller of
        /// <c>importVideoWallpaper</c> directly). Posts the work and returns at once: never blocks
        /// whichever thread calls this. Returns whether the switch was actually POSTED for
        /// dispatch -- never whether it finished, since the caller is told before the work item
        /// even runs -- so the HTTP endpoint can answer 503 instead of 202 when there is nothing
        /// to switch on this composition; the tray call site below ignores the return value and
        /// behaves exactly as it always has.
        /// </summary>
        /// <param name="phase">
        /// Recorded on every trace line this switch produces, so an HTTP-initiated switch reads
        /// distinctly from a tray pick (<c>phase=http</c> vs the tray's own default
        /// <c>phase=pick</c>) without duplicating this whole method for one word. The restore
        /// branch, taken by either caller when the import fails and a previous video exists,
        /// always traces <c>phase=restore</c> regardless of what switched it -- restoring is the
        /// same fallback either way, not a per-caller outcome.
        /// </param>
        /// <param name="skipIfUnchanged">
        /// same-video-noop, S2 (snapshot comparison per noop-followups F1): HTTP only (decision 1
        /// -- a tray re-pick keeps reloading exactly as today, so <c>setVideoWallpaperPath</c>'s
        /// call site below never passes this). When true AND playback is genuinely active AND a
        /// fresh <c>SafeReadSnapshot</c> of <paramref name="path"/> equals <c>currentVideoSnapshot</c>
        /// -- the snapshot taken when that playback actually started -- the whole
        /// stop/import/persist/(re)activate sequence is skipped -- the request is still accepted
        /// for dispatch (this method still returns <see langword="true"/>), it just does nothing
        /// once it runs.
        /// </param>
        /// <remarks>
        /// Checks its own collaborators the same way <see cref="ActivateVideoWallpaper"/> does,
        /// rather than trusting a caller's earlier check -- redundant for
        /// <c>setVideoWallpaperPath</c> below, which only ever reaches this far once it has
        /// already checked, the same redundancy the startup activation further down this method
        /// accepts by checking before calling <see cref="ActivateVideoWallpaper"/> itself.
        /// </remarks>
        bool SwitchVideoWallpaper(string path, string phase = "pick", bool skipIfUnchanged = false)
        {
            // S7 (wallpaper-scene-http-endpoint, R3-html-mode-video-switch-not-guarded): html mode
            // owns the wallpaper surface -- BOTH callers of this method (the tray pick's
            // setVideoWallpaperPath closure below, and HandleVideoWallpaperHttpSwitch right after
            // this method) reach it, and only it, to stop/import/persist/(re)activate a video, so
            // ONE guard here -- checked first, before the collaborator-null check that follows --
            // covers both entry points without duplicating it at either call site. False on every
            // video-mode call, exactly as before this task, so video-mode behaviour is untouched.
            // No import, no persist, no player touch happens below this line when it fires -- and
            // it fires BEFORE onVideoWallpaperThread ever posts anything, so nothing is queued
            // either. Traced with THIS caller's own phase, matching the file's existing
            // "video-wallpaper phase=<phase> ..." vocabulary (see ActivateVideoWallpaper below): a
            // skipped tray pick reads "phase=pick skipped reason=html-mode" and a skipped HTTP
            // switch reads "phase=http skipped reason=html-mode".
            if (wallpaperMode == WallpaperMode.Html)
            {
                desktopTrace?.Record($"video-wallpaper phase={phase} skipped reason=html-mode");
                return false;
            }

            // T4 (mini-scene-window): mini mode has no wallpaper surface at all, so a video can
            // neither play nor be imported -- skipped exactly like html mode, with its own reason.
            if (miniMode)
            {
                desktopTrace?.Record($"video-wallpaper phase={phase} skipped reason=mini-mode");
                return false;
            }

            if (videoWallpaperHost is null || videoWallpaperPlayer is null)
            {
                return false;
            }

            // T1 fix: the WHOLE sequence -- stop, import, persist, (re)activate -- now runs as
            // ONE work item on the video wallpaper thread, in that order. Bug 1 was exactly this
            // ordering: import ran on the tray thread BEFORE playback stopped, so Media
            // Foundation still held the fixed destination open and the copy threw a sharing
            // violation. Moving the whole sequence here, after the stop, also moves the
            // multi-gigabyte copy itself off the tray thread, which used to block the tray menu
            // for as long as the copy took.
            onVideoWallpaperThread(() =>
            {
                // same-video-noop, S2: read and checked HERE, inside the work item, for the same
                // reason `previous` below is -- work items run in order, so this sees whatever an
                // earlier queued switch actually landed, not a stale value read before this one was
                // even posted. Requires playback to be genuinely ACTIVE, not merely configured: a
                // died playback (attach or play failed last time) must still let a repeat request
                // revive it, exactly as it does today.
                //
                // noop-followups, F1 (R3-inplace-edit-hardlink): compares a FRESH read of the
                // REQUESTED path against the STALE currentVideoSnapshot captured when playback
                // started -- never a fresh-vs-fresh comparison, which a hard-linked import would
                // always pass regardless of an in-place edit (see VideoFileSnapshot's remarks). A
                // missing currentVideoSnapshot, a failed SafeReadSnapshot on the requested path
                // (missing file, access denied, or an injected reader throwing --
                // R3-predicate-throw-not-contained), or an unequal snapshot all read as "different"
                // and fall through to the ordinary switch below.
                if (skipIfUnchanged
                    && videoWallpaperActive.Value
                    && currentVideoSnapshot is { } activeSnapshot
                    && SafeReadSnapshot(path) is { } requestedSnapshot
                    && requestedSnapshot.Equals(activeSnapshot))
                {
                    desktopTrace?.Record($"video-wallpaper phase={phase} unchanged");
                    return;
                }

                // Idempotent and never throws, per the interface contract -- releases the fixed
                // destination file so the import below can overwrite it.
                videoWallpaperPlayer.Stop();

                // F1 (video-wallpaper-review-followups, R3-stale-active-after-failed-pick):
                // nothing is playing the instant Stop() returns, so the flag the watch tick
                // reads must say so immediately -- not only once an ActivateVideoWallpaper call
                // happens to run below. Every path that goes on to actually (re)play
                // (ActivateVideoWallpaper, on both the restore and the pick-success branches
                // further down) overwrites this with the real outcome; only the "import threw
                // and there is no previous path to restore" branch returns without calling it,
                // and this is what keeps that branch honest too. currentVideoSnapshot follows the
                // same rule (F1, noop-followups): no snapshot is ever kept for a video that is not
                // actually playing, and the same two ActivateVideoWallpaper branches below are what
                // set it back to a real value.
                videoWallpaperActive.Value = false;
                currentVideoSnapshot = null;

                // Read HERE, inside the work item, never on the caller's thread before posting it.
                // Work items run one at a time in order, so this sees whatever the switch queued
                // ahead of this one actually landed; read at call time, a second switch queued
                // behind a first-ever one would see null and have nothing to fall back to.
                var previous = currentVideoWallpaperPath;

                string imported;
                try
                {
                    imported = importVideoWallpaper(path);
                }
                catch (Exception error)
                {
                    // The constraint from the feature doc: a failed switch must never leave the
                    // wallpaper dead. No absolute paths in the trace line -- the picked path and
                    // the import destination both live under the user's profile. Falling back to
                    // the PREVIOUS video (if any) is what keeps this from being the empty desktop
                    // bug 1 itself reported; not persisting means the failed switch never gets
                    // remembered as if it had landed on disk.
                    desktopTrace?.Record(
                        $"video-wallpaper phase={phase} import-failed error={error.GetType().Name}");
                    if (previous is not null)
                    {
                        ActivateVideoWallpaper("restore", previous);
                    }

                    return;
                }

                persistVideoWallpaperPath?.Invoke(imported);
                currentVideoWallpaperPath = imported;

                // (Re)start playback with the IMPORTED path -- never the raw one the caller
                // passed in, since that is what actually landed on disk. Handles both the
                // first-ever switch (the startup activation below never ran, because no path was
                // configured yet) and every later re-switch identically: TryAttach is documented
                // idempotent and TryPlay is documented to tear down and restart cleanly, so
                // there is no need to branch on whether this is the first attach.
                ActivateVideoWallpaper(phase, imported);
            });

            return true;
        }

        /// <summary>
        /// V4: the HTTP video-wallpaper route's delegate, handed to <see
        /// cref="createLocalHttpCommandServer"/>'s <c>handleVideoWallpaperSwitch</c> parameter
        /// when <paramref name="httpServerEnabled"/> is on. Never calls
        /// <see cref="VideoWallpaperImport.Import"/> itself, and never any import logic directly
        /// -- it goes through <see cref="SwitchVideoWallpaper"/>, the SAME serialized path the
        /// tray uses, so a concurrent tray pick and HTTP request can never race the shared
        /// temp-name import (Review 2's R3-temp-sweep-races-concurrent-import constraint).
        /// </summary>
        /// <remarks>
        /// Answers "not available" (503) rather than dispatching when THIS composition has no
        /// dedicated video-wallpaper thread of its own (<paramref name="scheduleVideoWallpaperWork"/>
        /// unset) -- checked here, on the raw constructor parameter, before <see
        /// cref="SwitchVideoWallpaper"/> ever runs. Without a dedicated thread,
        /// <c>onVideoWallpaperThread</c> falls back to <c>onOwningThread</c>, which itself
        /// defaults to running inline on whichever thread calls it -- fine for a tray click (it
        /// always ran that way before this task), but it would mean the import's multi-gigabyte
        /// copy runs SYNCHRONOUSLY on this HTTP server's one request-handling thread, which <see
        /// cref="LocalHttpCommandServer"/>'s own contract for this delegate forbids. Answering 503
        /// is the least surprising choice for a composition that was never going to switch
        /// anything asynchronously in the first place, and it costs nothing beyond this one null
        /// check -- no new thread is spun up just to make the endpoint technically answer 202.
        /// </remarks>
        /// <remarks>
        /// S7 (wallpaper-scene-http-endpoint, R3-html-mode-video-switch-not-guarded): also answers
        /// 503 in html mode, exactly like the video route's video-mode-only contract requires. This
        /// method has no mode check of its own -- <see cref="SwitchVideoWallpaper"/>'s own guard,
        /// which fires first, covers it, the same guard the tray pick below relies on.
        /// </remarks>
        bool HandleVideoWallpaperHttpSwitch(string path) =>
            scheduleVideoWallpaperWork is null
                ? false
                : SwitchVideoWallpaper(path, phase: "http", skipIfUnchanged: true);

        /// <summary>
        /// S4 (wallpaper-scene-http-endpoint): the HTTP wallpaper-scene route's delegate, handed to
        /// <see cref="createLocalHttpCommandServer"/>'s <c>handleWallpaperSceneSwitch</c> parameter
        /// when <paramref name="httpServerEnabled"/> is on. <paramref name="name"/> is
        /// already validated against the closed allow-list by <see
        /// cref="WallpaperSceneHttpProtocol.TryValidate"/> before this runs -- <see
        /// cref="TryParseWallpaperScene"/> failing is only a defensive fallback, never expected in
        /// production.
        /// </summary>
        /// <remarks>
        /// Answers "not available" (503) rather than dispatching outside html wallpaper mode, or
        /// when this composition has no live scene switch to offer at all (<paramref
        /// name="switchHtmlWallpaperScene"/> unset -- no alert layer exists). Both checked BEFORE
        /// posting anything, mirroring <see cref="HandleVideoWallpaperHttpSwitch"/>'s own
        /// scheduleVideoWallpaperWork check. The actual switch (and, on success, the persist) runs
        /// on <c>onOwningThread</c> -- the STA UI thread <see cref="WebViewAlertLayerController"/>
        /// requires -- NEVER <c>onVideoWallpaperThread</c> (an MTA thread; WebView2 throws off its
        /// own dispatcher). Non-blocking, same contract as the video route: this returns whether the
        /// switch was accepted for dispatch, not its eventual outcome.
        /// </remarks>
        /// <remarks>
        /// S6 (wallpaper-scene-http-endpoint, R3-owning-thread-work-unguarded): the posted work runs
        /// AFTER the HTTP 202 reply already went out, so a throw here has nowhere left to go but the
        /// owning dispatcher -- guarded exactly like <see cref="SwitchVideoWallpaper"/>'s own posted
        /// work item guards <c>importVideoWallpaper</c>: caught, reported through
        /// <c>desktopTrace</c> by exception TYPE NAME ONLY (never <c>Message</c>, the same rule
        /// <c>SwitchVideoWallpaper</c>'s own <c>import-failed</c> line follows), and never rethrown.
        /// The switch and the persist are guarded SEPARATELY, each with its own trace tag, so a
        /// persist failure (the switch itself succeeded) never reads as a switch failure. A throwing
        /// switch returns early -- exactly like not calling <c>persistWallpaperScene</c> when the
        /// switch reports <see langword="false"/> above, nothing to persist means nothing runs.
        /// </remarks>
        bool HandleWallpaperSceneHttpSwitch(string name)
        {
            // T4: mini mode switches the corner window instead of the wallpaper alert layer; the rest
            // of the contract (202 now, switch + persist on the owning thread later) is identical.
            Func<WallpaperScene, bool>? switchScene = wallpaperMode switch
            {
                WallpaperMode.Html => switchHtmlWallpaperScene,
                WallpaperMode.HtmlMini => miniWindow is null ? null : miniWindow.SwitchScene,
                _ => null,
            };
            if (switchScene is null)
            {
                return false;
            }

            if (!TryParseWallpaperScene(name, out var scene))
            {
                return false;
            }

            onOwningThread(() =>
            {
                bool switched;
                try
                {
                    switched = switchScene(scene);
                }
                catch (Exception error)
                {
                    desktopTrace?.Record(
                        $"wallpaper-scene-http switch-failed error={error.GetType().Name}");
                    return;
                }

                if (!switched)
                {
                    return;
                }

                try
                {
                    persistWallpaperScene?.Invoke(scene);
                }
                catch (Exception error)
                {
                    desktopTrace?.Record(
                        $"wallpaper-scene-http persist-failed error={error.GetType().Name}");
                }
            });

            return true;
        }

        /// <summary>
        /// S4: maps the canonical lowercase name <see cref="WallpaperSceneHttpProtocol.TryValidate"/>
        /// already validated back to its <see cref="WallpaperScene"/> member. This class references
        /// <c>CosmicWin.Interop</c>'s <see cref="WallpaperSceneHttpProtocol"/> for the closed
        /// allow-list, but that project cannot reference THIS enum back, so the mapping lives here --
        /// the one place a protocol/settings string ever becomes a <see cref="WallpaperScene"/>, the
        /// same role <see cref="CosmicWin.App.Settings.Parse"/>'s own private
        /// <c>TryReadWallpaperScene</c> plays for the settings file.
        /// </summary>
        static bool TryParseWallpaperScene(string name, out WallpaperScene scene)
        {
            switch (name)
            {
                case "processing":
                    scene = WallpaperScene.Processing;
                    return true;
                case "explorer":
                    scene = WallpaperScene.Explorer;
                    return true;
                case "idle":
                    scene = WallpaperScene.Idle;
                    return true;
                case "raphael":
                    scene = WallpaperScene.Raphael;
                    return true;
                default:
                    scene = WallpaperScene.Processing;
                    return false;
            }
        }

        string HandleAlertCommand(string text)
        {
            var parsed = AlertCommandParser.Parse(text);
            if (!parsed.Success || parsed.Command is null)
            {
                var error = parsed.Error ?? "alert command could not be parsed";
                desktopTrace?.Record($"alert rejected: {error}");
                return AlertPipeProtocol.FormatError(error);
            }

            lock (alertQueueLock)
            {
                if (alertQueue is null)
                {
                    return AlertPipeProtocol.FormatError("alerts are disabled");
                }

                alertQueue.Enqueue(parsed.Command, alertClock.GetUtcNow());
            }

            // T9d (webview-alert-layer): HandleAlertCommand runs on the pipe server thread -- do not
            // wait for the next 400ms watch tick to show a newly queued alert. UpdateAlertOverlay
            // itself must stay on the UI thread (it touches the WebView layer and the overlay), and
            // must not race the watch tick's own call -- onOwningThread already serializes both onto
            // the same dispatcher, so posting here is safe.
            onOwningThread(UpdateAlertOverlay);

            return AlertPipeProtocol.OkReply;
        }

        void UpdateAlertOverlay()
        {
            if (!alertsEnabled || alertQueue is null)
            {
                return;
            }

            ActiveAlert? active;
            lock (alertQueueLock)
            {
                // T10 (live-alert-wallpaper): the real predicate is "a video is playing AND nothing
                // fullscreen covers the primary monitor" -- T9 proved the T8 fallback (video playing
                // alone) plays an alert out unseen under a fullscreen window instead of holding it.
                // alertDesktopVisible, when supplied, still overrides this composition entirely (the
                // seam every test predating T10 uses); isPrimaryMonitorCovered is the new, narrower
                // seam for the coverage half alone, wired to the real Win32 check by WireProduction.
                // D3 (html-wallpaper-demo): htmlWallpaperActive generalizes the SAME "is the
                // wallpaper actually up" signal videoWallpaperActive already provides here -- in html
                // mode nothing ever sets videoWallpaperActive (no player is ever started, by design),
                // so without this an alert command would sit pending forever and eventually expire,
                // contradicting the whole point of the demo switch (alerts still toggle the overlay).
                // Composition wiring only: AlertQueue.Advance and PrimaryMonitorFullscreenDetector
                // stay exactly as they are.
                // T4: the mini window is TOPMOST, so nothing can cover it -- unlike a wallpaper, which a
                // fullscreen window hides -- and its own readiness is the window's IsReady: shown AND its
                // browser attached (a page still loading holds the alert as pending itself).
                var desktopVisible = (alertDesktopVisible?.Invoke()
                    ?? (miniMode
                        ? miniWindowActive.Value && miniWindow is { IsReady: true }
                        : (videoWallpaperActive.Value || htmlWallpaperActive.Value)
                            && !(isPrimaryMonitorCovered?.Invoke() ?? false)))
                    && (alertStart is null || alertRendererReady?.Invoke() != false);
                active = alertQueue.Advance(alertClock.GetUtcNow(), desktopVisible);
            }

            // alertStart is the wallpaper's preloaded WebView2 alert layer, or the mini window in mini mode
            // (the Direct2D overlay is gone). Unset only in tests that never exercise the overlay at all.
            if (alertStart is null) return;

            if (ReferenceEquals(active, displayedAlert)) return;
            if (displayedAlert is not null)
            {
                alertEnd?.Invoke();
                // Torn down, not yet replaced: leave nothing marked displayed until a start
                // below actually succeeds, so a failed retry never re-ends the same layer.
                displayedAlert = null;
            }
            if (active is null) return;
            // The queue's deadline starts when Advance promotes the command, not when the
            // renderer starts. Never grant an extra watch interval to a late UI tick.
            var remaining = active.Command.Duration - (alertClock.GetUtcNow() - active.StartedAt);
            if (remaining <= TimeSpan.Zero) return;
            var failed = active.Command.Groups.Any(group => group.Kind == AlertKind.Failed);
            if (failed && !ReferenceEquals(shakenAlert, active))
            {
                // Shake once per alert: startAlertLayer below can fail and retry this same
                // alert across several ticks, and the shake must not repeat on retry.
                shakenAlert = active;
                shakeAlertVideo?.Invoke(TimeSpan.FromMilliseconds(120));
            }
            try
            {
                // Started before recording displayedAlert on purpose: on failure the alert
                // must not count as shown, so the next tick retries it while duration remains.
                // The alert page itself waits 120ms before revealing, so shaking here ahead of
                // a not-yet-confirmed start is harmless.
                // alert-tile-mosaic: the full ordered tile list/grid (AlertTileLayout) replaces the
                // single collapsed "failed"/"warning" kind this used to pass -- every per-kind count
                // the parser already accepted now reaches the layer instead of being thrown away.
                // Gap is read from TreeArranger.Gap HERE, at show time, so a settings change takes
                // effect on the next alert without AlertShowRequest/the preload state/the controller
                // ever having to know about settings.
                var layout = AlertTileLayout.From(active.Command);
                var tiles = layout.Tiles.Select(kind => kind == AlertKind.Failed ? "failed" : "warning").ToArray();
                // T7 (alert-tile-mosaic, 2026-09-26): the work area is read HERE, at show time, same
                // reason as Gap just below -- the taskbar can move/auto-hide between wiring and an
                // alert firing. treeManager.Primary is a live lookup (TreeManager.Primary), and the
                // IDisplay object it returns updates itself IN PLACE on every watch tick's
                // refreshDisplays() call (Win32Display.Refresh), so this always sees the latest known
                // reading without a fresh GetMonitorInfo call of its own -- exactly the "existing
                // work-area tracking code" the task asks to reuse. Never allowed to fail the alert:
                // any exception here (there should never be one against these plain property reads)
                // degrades to AlertLayerWorkArea.Unavailable, which the page already treats as "lay
                // out on the whole canvas", the pre-T7 behaviour.
                AlertLayerWorkArea workArea;
                try
                {
                    var alertDisplay = treeManager.Primary;
                    workArea = AlertLayerWorkArea.Resolve(alertDisplay.Bounds, alertDisplay.WorkArea);
                }
                catch (Exception ex)
                {
                    desktopTrace?.Record($"alert-layer-workarea-failed {ex.GetType().Name}: {ex.Message}");
                    workArea = AlertLayerWorkArea.Unavailable;
                }
                // R3-negative-gap-blocks-alert: TreeArranger.Gap is a shared mutable static nothing
                // stops another caller from setting negative (T5's settings.conf `gap` key itself
                // rejects anything outside 0-64, but that guard lives in Settings.Parse, not on the
                // static field) -- WebViewAlertLayerController.Start throws on a negative Gap, so
                // clamped here rather than letting a stray negative value take the whole alert down.
                alertStart(new AlertShowRequest(
                    tiles, layout.Columns, layout.Rows, Math.Max(0, TreeArranger.Gap),
                    Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)),
                    workArea.Left, workArea.Top, workArea.Width, workArea.Height));
            }
            catch (Exception ex) when (IsRecoverableFailure(ex))
            {
                // Recorded, not swallowed, and not re-thrown into the watch tick: an escaping
                // exception here would also skip that tick's UpdateFocusBorder call.
                desktopTrace?.Record($"alert-layer-start-failed {ex.GetType().Name}: {ex.Message}");
                return;
            }
            displayedAlert = active;
        }

        // The catch filter for one feature's recoverable failure (alert layer, mini window, focus border,
        // reload gap...): WebView2/COM/dispatcher-state errors are handled and traced by the caller,
        // while the process-corrupting types are named so they are never mistaken for one feature's
        // problem. (The CLR cannot always deliver StackOverflow/AccessViolation to a catch at all; this
        // filter only keeps them out of the handled class.)
        static bool IsRecoverableFailure(Exception ex) =>
            ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

        /// <summary>
        /// Takes the border off the screen right now, without deciding anything about where it
        /// belongs next.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called on the way IN to a desktop change, so nothing stale can be seen on a desktop that
        /// has not arrived yet. <see cref="IFocusBorder.Hide"/> is a bare SetWindowPos with no WPF
        /// object behind it, so it needs no marshalling -- which is the whole reason this can run
        /// before the switch instead of being queued behind it.
        /// </para>
        /// <para>
        /// It RECORDS as well as hides, and that is not decoration. RecordBorderDecision suppresses
        /// a repeat, so a border hidden here and re-shown on the same rectangle a moment later
        /// would trace nothing at all -- and a border path that quietly does nothing reads exactly
        /// like a broken one. This repository has already been bitten by that twice.
        /// </para>
        /// </remarks>
        void ReleaseBorder(string why)
        {
            if (focusBorder is null || !borderEnabled)
            {
                return;
            }

            RecordBorderDecision($"released: {why}");
            focusBorder.Hide();
        }

        // Late-bound on purpose. The border is drawn by a local function that has to exist before
        // the adapter does -- it is handed to TreeManager as a callback on the way past -- so the
        // question it asks is routed through a variable rather than through the adapter itself.
        Func<nint, bool> isConstrained = _ => false;

        // Handed to the collaborators that reflow WITHOUT a chord -- a window closing, one leaving
        // for another desktop, a group collapsing -- which is precisely the set that had no path to
        // the border at all and left it a full reconciliation interval behind.
        //
        // Filtered by what actually MOVED, not merely by "a reflow happened". A reflow re-applies
        // geometry to every tile, so an unfocused window snapping back from a drag runs one too, and
        // answering that would place the border on its own unchanged rectangle once per frame of
        // somebody else's animation.
        //
        // Nothing focused is NOT that case, and it is the one the moved list cannot express. Closing
        // the framed window does not move it, it ends it: its handle is in no moved list, so a plain
        // "did the focused window move" test reads false and leaves the border drawn over a
        // rectangle whose window is gone until the next tick. So the early return asks a narrower
        // question -- is there a focused window that DIDN'T move -- and everything else, including
        // having no focused window at all, goes to UpdateFocusBorder, which answers that state by
        // drawing nothing. The extra work when nothing is focused is a Hide the tick was already
        // making every interval anyway.
        void AfterArrange(IReadOnlyList<nint> moved)
        {
            if (executor.ResolveFocusedLeaf() is { } leaf && !moved.Contains(leaf.Window.Handle))
            {
                return;
            }

            onOwningThread(UpdateFocusBorder);
        }

        // Built by this method's CALLER, so it cannot take the callback at construction. Its own
        // reflows are the hotplug and work-area ones -- dormant today, live the moment MM-2/MM-3
        // land, and there is no reason to leave a known-blind call site behind for them to find.
        treeManager.AfterArrange = AfterArrange;

        // TWO switches, ONE gate, and the collapse is deliberate. Pausing stops everything; turning
        // tiling off stops the layout and leaves the desktop chords alone -- but to anything that
        // MOVES a window those are the same instruction, and giving the adapters a second flag to
        // check would be two chances to check only one of them.
        //
        // The difference between the two switches lives where it belongs instead: in the chord path,
        // which is the only place that can tell a desktop chord from a layout one.
        bool LayoutIsFrozen() => hook.IsPaused || !tiling;

        var sessionAdapter = new MultiMonitorWorkspaceAdapter(
            workspace, treeManager, registry, () => exceptionStore.Current, LayoutIsFrozen,
            executor.ResolveFocusedLeaf, AfterArrange)
        {
            ResolveWindowDesktop = resolveWindowDesktop,
            Trace = File.Exists(TraceMarkerPath) ? desktopTrace : null,
        };

        if (virtualDesktops is { } desktopsForArrivals)
        {
            sessionAdapter.ResolveUserDesktop = () => lastDesktop;
            sessionAdapter.SendWindowToDesktop = (handle, desktop) =>
            {
                // Positional, so only the desktop this composition is actually tracking can be
                // aimed at -- an id it has no index for is not something to go looking for.
                if (desktop != lastDesktop || lastDesktopIndex < 1)
                {
                    return false;
                }

                var moved = desktopsForArrivals.TryMoveWindowTo(handle, lastDesktopIndex);
                if (moved)
                {
                    // The move alone leaves the user wherever the window took them. Putting the view
                    // back is the other half of the reported defect, and costs nothing when the
                    // shell never moved them -- switching to the desktop already shown is a no-op.
                    desktopsForArrivals.TrySwitchTo(lastDesktopIndex);
                }

                desktopTrace?.Record(
                    $"ArrivingWindow hwnd=0x{handle:X} sentTo={lastDesktopIndex} ok={moved} " +
                    $"error={desktopsForArrivals.LastError ?? "(none)"}");

                return moved;
            };
        }
        executor.WindowMovedToDesktop = sessionAdapter.RehomeToDesktop;

        // Registry first, workspace second. The registry answers for a tiled window immediately;
        // the workspace tracks every top-level window, which is the only place a NON-tiled one can
        // be found. Snapshot is walked rather than indexed because these run once per chord, and a
        // dictionary kept in step would be more state to get wrong than the walk costs.
        IWindow? resolveAnyWindow(nint handle) =>
            registry.TryGetWindow(handle, out var tracked) && tracked is { IsAlive: true }
                ? tracked
                : workspace.Snapshot.FirstOrDefault(candidate => candidate.Handle == handle)
                    is { IsAlive: true } other ? other : null;

        // One lookup, two verbs -- put back on a window whose move the shell refused, and asked to
        // close by Alt+Q. Both have to reach windows the tree does not contain.
        executor.ActivateUntrackedWindow = handle => resolveAnyWindow(handle)?.TryActivate() ?? false;
        executor.CloseWindowAt = handle => resolveAnyWindow(handle)?.TryClose() ?? false;

        // Three verbs now, and this is the read-only one: WHERE a window is, including one the tree
        // does not hold. A focus chord pressed while an untiled window has the foreground measures
        // the direction from here, which is the difference between landing on the tile the user
        // pointed at and landing on whatever survived.
        executor.ResolveWindowBounds = handle => resolveAnyWindow(handle)?.Bounds;

        // Where a window LIVES, which the untiled handover has to ask directly. The tiled one gets
        // the same answer for free -- its trees are keyed by desktop, so being in one is the
        // answer -- and with no tree there is nothing to read it from.
        executor.ResolveWindowDesktop = resolveWindowDesktop;

        // The fourth, and the only one that reads the adapter rather than the workspace: these are
        // measured from how a window BEHAVED, which is the adapter's business alone.
        executor.ResolveSizeLimits = sessionAdapter.LimitsOf;

        // The resize chord's answer when there is no tree to move a boundary in -- which, with the
        // tiling switch off, is every window on the desktop. The arithmetic is
        // FloatingResize.Apply's; everything here is finding the three things it needs and refusing
        // the windows it must never be pointed at.
        executor.ResizeFloatingWindow = (handle, direction) =>
        {
            if (resolveAnyWindow(handle) is not { IsAlive: true, CanReposition: true } window)
            {
                return;
            }

            // The FULL exclusion rule, user list included -- unlike the focus border, which reads
            // only the automatic half. The difference is that this one MOVES the window. The
            // automatic half keeps the chord off shell chrome: the taskbar is the foreground window
            // the moment it is clicked, and it is visible and unowned, so nothing else would. The
            // user's own list is what "leave this app alone" means, and a resize is exactly the
            // kind of touching it asks CosmicWin not to do.
            if (WindowFilters.IsExcluded(WindowDescriptorBuilder.Build(window), exceptionStore.Current))
            {
                return;
            }

            var display = treeManager.ResolveDisplay(window.Bounds);
            if (FloatingResize.Apply(
                    window.Bounds, display.WorkArea, direction,
                    LayoutTree.DefaultResizeStep, sessionAdapter.LimitsOf(handle)) is { } resized)
            {
                window.SetPosition(resized);
            }
        };

        // The focus chord's answer when there is no tree to walk -- FloatingFocus.Toward reads this
        // once per chord for its candidate list. Left unset when zOrder is null, exactly like every
        // other floating-mode delegate above: unwired, ActionExecutor's own untiled focus branch
        // stays the no-op it already was for every caller that has not opted in.
        if (zOrder is not null)
        {
            executor.ResolveFloatingWindows = () => zOrder()
                .Select(handle => (Handle: handle, Window: resolveAnyWindow(handle)))
                // ALIVE only, the same liveness gate every other resolution in this file applies --
                // a handle Windows has already reused for something else must not be handed to the
                // geometry as if it were still the window that used to live there.
                .Where(candidate => candidate.Window is { IsAlive: true })
                // The FULL exclusion rule, user list included -- the same reasoning written above
                // executor.ResizeFloatingWindow, and for the same reason it applies here too: this
                // is a chord that MOVES focus onto whatever it names, and the taskbar (auto-excluded,
                // visible and unowned the instant it is clicked) and the user's own "leave this app
                // alone" list must both be honoured, not only the automatic half the focus border
                // reads.
                .Where(candidate => !WindowFilters.IsExcluded(
                    WindowDescriptorBuilder.Build(candidate.Window!), exceptionStore.Current))
                .Select(candidate => (candidate.Handle, candidate.Window!.Bounds))
                .ToList();
        }

        // Deliberately NOT filtered to the origin's own monitor, unlike the tiled path's
        // NearestTileToward, which searches only trees.ResolveDisplay(from). That restriction is a
        // property of the TREE -- a tree belongs to one display, so its own leaves are the only
        // candidates it could ever offer -- not a property of the question being asked. With no
        // layout in force there is no tree to be scoped to, and a window sitting on the monitor to
        // the right genuinely IS the window to the right; refusing to look past a display edge that
        // exists only in the tiled model would make this path answer a narrower question than the
        // one the user actually asked by pressing a direction.

        isConstrained = sessionAdapter.IsConstrained;
        workspace.Open();
        hook.Start();

        // Stated at STARTUP, and stated even when it is null. Left to the first repaint, the first
        // window framed after launch would wear the accent for a moment before the stored colour
        // caught up; and saying "the accent" out loud keeps the two paths identical, so the one
        // nobody exercises cannot rot.
        onOwningThread(() => focusBorder?.UseColor(borderColor));

        // Everything that has to happen the moment the layout is put back on duty.
        void ResumeTiling()
        {
            // The loop below lays every display out on its CURRENT work area, which is all a
            // change that arrived while tiling was off was waiting for. Left in the list, the next
            // tick would move every window a second time to where it already is.
            displaysAwaitingReflow.Clear();

            // The windows that opened while it was off were refused by the adapter, and the
            // workspace considers them announced -- so nothing will ever mention them again. Asking
            // for them BY NAME is the only route back; one already tiled costs a single lookup.
            sessionAdapter.AdoptOpenWindows();

            // The windows tiled BEFORE it went off were handed their maximize box back then and are
            // never announced again, so this is the only thing that takes it away a second time.
            // Read BEFORE the batch: restoring a maximized window activates it.
            sessionAdapter.ApplyMaximizeBlock(foreground.GetForegroundHandle());

            // Then EVERY display, not only the ones that gained a window. While tiling was off the
            // user was free to drag and resize with the mouse, and switching it back on is a request
            // to put the layout back -- which for a display where nothing opened or closed is a
            // request nothing else in this composition would ever make.
            RearrangeEveryDisplay();
        }

        // Extracted from the loop ResumeTiling used to run inline, so a live settings-reload gap
        // change (ReloadGap, below) can put the new spacing on screen through the SAME "walk every
        // display and arrange it" step, rather than a second copy of this loop (T5, alert-tile-mosaic).
        void RearrangeEveryDisplay()
        {
            foreach (var display in treeManager.Displays)
            {
                if (treeManager.TryGetTree(display, out var tree) && tree is not null)
                {
                    TreeArranger.ArrangeAndPosition(
                        tree, registry, WorkAreaResolver.Resolve(display), AfterArrange);
                }
            }
        }

        // T5 (alert-tile-mosaic): WE-3's "Reload" trigger re-reads settings.conf's `gap` key here
        // and puts it into effect. TreeArranger.Gap is the one shared static both the tiling engine
        // and the alert mosaic read, so this is the one place a live gap change has to land. Unset
        // loadGap (every composition that predates this parameter) means this never runs, matching
        // the exceptions-only Reload this project has always had.
        void ReloadGap()
        {
            if (loadGap is null)
            {
                return;
            }

            try
            {
                TreeArranger.Gap = loadGap();

                // Mirrors ToggleTiling's OFF branch, just below: while tiling is off, every window is
                // deliberately left exactly where the layout last put it, and a live gap change must
                // not reach in and move windows a mode promised not to touch. The new value still
                // lands in TreeArranger.Gap, so it is there the moment tiling resumes, and it already
                // reaches the alert mosaic (read at show time, above) regardless of the tiling switch.
                if (tiling)
                {
                    RearrangeEveryDisplay();
                }
            }
            catch (Exception ex) when (IsRecoverableFailure(ex))
            {
                // T13 (alert-tile-mosaic, review R4-reload-swallow-without-trace): traced HERE, not
                // relying on CompositionRoot.Reload's own try/catch around reloadGap?.Invoke() below
                // -- that one only ever wraps the SYNCHRONOUS call to onOwningThread(ReloadGap), which
                // in production (scheduleOnOwningThread: RunOnUiThread, i.e. Dispatcher.BeginInvoke)
                // returns as soon as this method is QUEUED, before its body ever runs. By the time a
                // failure could happen in here, CompositionRoot.Reload has already returned
                // successfully; without this catch the failure would reach nothing but WPF's
                // unhandled-dispatcher-exception path instead of a trace line.
                CompositionRoot.ReportSwallowedFailure(desktopTrace, $"reload-gap-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        // TC-3-W1: Salir stops the logon trigger BEFORE tearing the process down -- after shutdown
        // there is no guarantee anything still runs. Disable, not uninstall: TC-3 says "disable the
        // Scheduled Task trigger" where ES-4 says "remove", so quitting once must not throw the
        // user's installation away.
        var trayController = CompositionRoot.BuildTrayMenuController(
            hook, exceptionStore, loadExceptions,
            getFocusBorder: () => borderEnabled,
            setFocusBorder: enabled =>
            {
                borderEnabled = enabled;

                // Persisted BEFORE the redraw, so the choice survives even if the refresh throws.
                persistFocusBorder?.Invoke(enabled);

                // Straight through the ordinary refresh, on the thread that owns the overlay. A
                // menu click must reach the screen now, not on the next tick -- a setting the user
                // waits half a second to see reads as one that did not work.
                onOwningThread(UpdateFocusBorder);
            },
            getTiling: () => tiling,
            setTiling: enabled =>
            {
                tiling = enabled;

                // Persisted BEFORE the layout is put back, the same order the border toggle uses
                // and for the same reason: the choice must survive even if the reflow throws.
                persistTiling?.Invoke(enabled);

                if (enabled)
                {
                    // On the thread that owns the trees and the overlay. This arrives from a tray
                    // click, and a reflow places windows and refreshes the border -- the WinEvent
                    // hook's own thread does both everywhere else in this file.
                    onOwningThread(ResumeTiling);
                }
                else
                {
                    // The one thing turning it OFF does: the maximize buttons come back. A mode whose
                    // promise is that the layout no longer interferes cannot leave windows with a
                    // disabled button for it. Same thread as the trees the adapter reads.
                    onOwningThread(sessionAdapter.ReleaseMaximizeBlock);
                }

                // Nothing else changes on purpose. Every window is left exactly where the layout
                // last put it, which is the honest starting point for a mode whose whole promise is
                // that nothing moves any more.
            },
            getBorderColor: () => borderColor,
            setBorderColor: rgb =>
            {
                borderColor = rgb;

                // Persisted before the repaint, the same order the toggle uses and for the same
                // reason: the choice must survive even if the refresh throws.
                persistBorderColor?.Invoke(rgb);

                // On the overlay's own thread. A WPF window may only be touched by the thread that
                // created it, and this arrives from a tray click.
                onOwningThread(() => focusBorder?.UseColor(rgb));
            },
            setVideoWallpaperPath: path =>
            {
                // T4: BEFORE the collaborator check below. Mini production wires no host and no
                // player, and that branch would otherwise import (copy) the picked video and persist
                // its path -- a pick that must do nothing in a mode with no wallpaper to play it on.
                if (miniMode)
                {
                    desktopTrace?.Record("video-wallpaper phase=pick skipped reason=mini-mode");
                    return;
                }

                if (videoWallpaperHost is null || videoWallpaperPlayer is null)
                {
                    // Nothing to stop or (re)play -- the same import+persist a composition with no
                    // playback collaborators wired has always done, inline on the tray thread.
                    // Import runs UNCONDITIONALLY, on its own statement: importVideoWallpaper(path)
                    // is a real side effect (the file copy), and folding it into
                    // persistVideoWallpaperPath?.Invoke(importVideoWallpaper(path)) would let the
                    // null-conditional short-circuit skip evaluating it entirely whenever no persist
                    // delegate is wired, exactly the composition every pre-T1 test with no persist
                    // callback exercises.
                    var importedInline = importVideoWallpaper(path);
                    persistVideoWallpaperPath?.Invoke(importedInline);
                    return;
                }

                // The full stop/import/persist/(re)activate sequence is SwitchVideoWallpaper,
                // right after ActivateVideoWallpaper -- the tray's own operation, callable from
                // elsewhere too.
                SwitchVideoWallpaper(path);
            },
            exit: () =>
            {
                disableTaskTrigger();
                shutdown();
            },
            // On the owning thread, the same one ResumeTiling's onOwningThread(ResumeTiling) uses a
            // few lines above -- this arrives from a tray click too, and ReloadGap can rearrange
            // trees and reach the overlay through AfterArrange (T5, alert-tile-mosaic).
            reloadGap: loadGap is null ? null : () => onOwningThread(ReloadGap),
            // T11 (alert-tile-mosaic): so a throwing half of Reload is reported the same way every
            // other recoverable per-tick failure in this file already is, instead of vanishing.
            desktopTrace: desktopTrace);
        // Alt+T lands on the SAME toggle the tray item clicks, rather than on a second copy of the
        // flip. Everything that makes the switch honest -- persisting it, and putting the layout
        // back when it comes on -- lives in the setTiling closure above, and a chord reaching past
        // it would be a mode that forgets itself on restart and leaves the windows where they lay.
        //
        // Wired HERE, not where the executor is built, because the controller that owns the toggle
        // does not exist yet up there.
        executor.ToggleTilingRequested = () => trayController.ToggleTiling();

        // T5 (mini-scene-window): Alt+M walks the mini window clockwise to the next corner. The move
        // and the corner bookkeeping run on the owning UI thread (the window's owner); the chord
        // itself arrives on a pool thread. Outside mini mode there is no window and the chord is a
        // traced no-op, so a stray press never persists a corner nobody can see.
        void CycleMiniCorner()
        {
            if (!miniMode || miniWindow is null)
            {
                desktopTrace?.Record(
                    $"mini-corner cycle skipped reason={(miniMode ? "no-window" : "not-mini-mode")}");
                return;
            }

            // A window that failed to show, or whose browser is not attached (yet, or any more), has no
            // corner to move: keep the configured one instead of persisting a corner nobody can see.
            if (!miniWindowActive.Value || !miniWindow.IsReady)
            {
                desktopTrace?.Record("mini-corner cycle skipped reason=not-ready");
                return;
            }

            var next = MiniWindowPlacement.Next(currentMiniPosition);
            try
            {
                miniWindow.MoveTo(MiniPlacement(next));
            }
            catch (Exception error) when (IsRecoverableFailure(error))
            {
                desktopTrace?.Record($"mini-corner cycle move-failed error={error.GetType().Name}");
                return;
            }

            currentMiniPosition = next;
            try
            {
                persistMiniPosition?.Invoke(next);
            }
            catch (Exception error) when (IsRecoverableFailure(error))
            {
                desktopTrace?.Record($"mini-corner cycle persist-failed error={error.GetType().Name}");
            }
        }

        executor.CycleMiniCornerRequested = () => onOwningThread(CycleMiniCorner);
        var tray = buildTray(trayController);

        _ = dispatcher.RunAsync(CancellationToken.None);

        if (alertsEnabled)
        {
            var serverFactory = createAlertCommandServer
                ?? ((pipeName, handler, diagnostic) => new NamedPipeAlertCommandServer(pipeName, handler, diagnostic));
            alertServer = serverFactory(AlertPipeName.Resolve(), HandleAlertCommand,
                message => desktopTrace?.Record(message));
            alertServer.Start();
            // T9c: preload the alert layer once, on the owning UI thread, rather than waiting for
            // the first alert command -- the whole point of the persistent-preload fix.
            if (preloadAlertLayer is not null) onOwningThread(preloadAlertLayer);
        }

        // The shared HTTP server starts when http-server is on and serves every route. Only the
        // alerts route also needs alertsEnabled (see HandleAlertCommand above); when alerts are
        // off it is left out of the table and answers as it does for a disabled feature.
        var alertHttpRouteOn = alertsEnabled;
        if (httpServerEnabled)
        {
            try
            {
                // The pipe above (when alertsEnabled) has already started by the time this runs, so
                // any failure below -- a bad port, a listener that cannot bind, the token file being
                // unreadable -- is caught and traced here rather than left to unwind Wire and take the
                // pipe down with it. The two servers stay independent on purpose.
                var loadToken = loadAlertHttpToken
                    ?? (() => AlertHttpTokenFile.LoadOrCreate(message => desktopTrace?.Record(message)));
                var token = loadToken();
                if (token is null)
                {
                    desktopTrace?.Record("alert-http token unavailable, HTTP alert endpoint not started");
                }
                else
                {
                    var httpServerFactory = createLocalHttpCommandServer
                        ?? ((port, t, handler, diagnostic, videoSwitch, sceneSwitch) =>
                            new LocalHttpCommandServer(
                                port, t, handler, diagnostic, videoSwitch,
                                handleWallpaperSceneSwitch: sceneSwitch));
                    httpAlertServer = httpServerFactory(
                        httpServerPort, token,
                        alertHttpRouteOn ? HandleAlertCommand : null,
                        message => desktopTrace?.Record(message),
                        HandleVideoWallpaperHttpSwitch,
                        HandleWallpaperSceneHttpSwitch);
                    httpAlertServer.Start();
                    // H5b: Start() never throws -- a port already in use is reported by the server
                    // itself as "alert http: failed to start listening ..." through this same sink
                    // -- so these lines must not claim the endpoint is listening.
                    if (alertHttpRouteOn)
                    {
                        desktopTrace?.Record($"alert-http start requested port={httpServerPort}");
                    }

                    // V4: which routes ended up in the routing table. The video and scene routes are
                    // always registered now (http-server is the one switch for the whole server), so
                    // their flags are constants kept in the line for the readers of this trace; only
                    // the alerts route still depends on alertsEnabled.
                    desktopTrace?.Record(
                        $"http-server start requested port={httpServerPort} " +
                        $"alerts-route={alertHttpRouteOn} video-route=True scene-route=True");
                }
            }
            // Same corruption-class exclusion IsRecoverableFailure already applies to a
            // per-alert render failure below: a bad port or a listener refusal is this server's
            // problem, not a reason to treat the whole composition as unsafe to continue.
            catch (Exception ex) when (IsRecoverableFailure(ex))
            {
                desktopTrace?.Record($"alert-http-start-failed {ex.GetType().Name}: {ex.Message}");
            }
        }

        // WT-1: SetWinEventHook is a best-effort notifier, not a guarantee -- a window created
        // hidden, an event dropped under load, or a hook briefly not pumped all leave the tree
        // disagreeing with the desktop, and nothing else ever looks again.
        string? lastReportedUnmatched = null;
        var lastReportedDropped = 0;
        var lastReportedReinstalls = 0;

        // The arriving desktop's own layout, applied to the work area in force NOW. Its windows
        // were left exactly where they were when the user walked away, so without this they would
        // still be wearing the previous desktop's geometry.
        void ApplyArrivingLayout()
        {
            // The one layout path a desktop chord can still reach with tiling off, and it had to be
            // stopped here rather than at the chord: the desktop chords are deliberately answered in
            // that mode, and this hangs off a successful one. Caught by a fact -- Alt+2 with tiling
            // off was placing two windows -- which is exactly the shape of defect a mode switch
            // wired at only one of its seams produces.
            //
            // Paused counts too, and that closes a smaller pre-existing hole on the way past: the
            // tick calls this for a switch CosmicWin did not make, so Win+Ctrl+arrow while paused
            // used to re-lay the arriving desktop that nothing was supposed to be touching.
            if (LayoutIsFrozen())
            {
                return;
            }

            if (treeManager.TryGetTree(treeManager.Primary, out var arriving) && arriving is not null)
            {
                TreeArranger.ArrangeAndPosition(
                    arriving, registry, WorkAreaResolver.Resolve(treeManager.Primary), AfterArrange);
            }
        }

        // Applied on the chord itself, not left to the timer. The timer remains the safety net for
        // a switch CosmicWin did not make -- Win+Ctrl+arrow, or Task View -- but waiting for it
        // after our own chord showed the user a loose window for up to a full interval.
        // Let go on the way IN. The border used to be refreshed on the way out, through AfterAction,
        // which runs after the shell has already changed desktops, after the arriving layout, and
        // after the handover's activations -- a window of time bounded only by how slow those are.
        // Nothing stale can be seen on a desktop that has not arrived yet.
        executor.BeforeDesktopChange = () => ReleaseBorder("the desktop is about to change under it");

        executor.DesktopSwitched = () =>
        {
            // Before the layout, and not left to the tick: until this runs, "the desktop the user is
            // on" still names the one they just left, and the next window to open would be sent back
            // there -- the reported defect, re-created by its own fix.
            if (virtualDesktops is not null)
            {
                lastDesktop = virtualDesktops.CurrentDesktopId;
                lastDesktopIndex = virtualDesktops.CurrentIndex;
            }

            ApplyArrivingLayout();
        };

        // Called from BOTH the chord and the tick, and it must be both. The chord is what makes the
        // border keep up -- Alt+O moves every window at once, and waiting for the tick left the
        // border on the old rectangle for up to half a second. The tick is the safety net for the
        // changes no chord caused: a mouse click landing on another window.
        void UpdateFocusBorder() => DrawFocusBorder(live: null);

        /// <param name="live">
        /// A rectangle the framed window is passing THROUGH, mid hand-drag, when there is one.
        /// Null everywhere else, which means "read it from the window", and that is the ordinary
        /// case: the window's own bounds are the answer for every change except the one the
        /// workspace deliberately withholds until the user lets go.
        /// </param>
        void DrawFocusBorder(Rectangle? live)
        {
            if (focusBorder is null)
            {
                return;
            }

            // Turned off, it HIDES rather than merely skipping. The overlay is created once and
            // reused forever, so a switch that only stopped drawing would strand the last frame it
            // drew on screen -- an outline around a window nobody is on.
            if (!borderEnabled)
            {
                RecordBorderDecision("hidden: the focus border is turned off");
                focusBorder.Hide();
                return;
            }

            // Resolved STRICTLY from the real foreground, never through
            // executor.ResolveFocusedLeaf(). That one deliberately falls back to the last known leaf
            // when the foreground is untracked -- its remarks name "a dialog or a non-tiled app" --
            // so a focus chord still works from outside the tiled world instead of being dropped.
            //
            // Right for a chord, wrong here. Reported with Sticky Notes, listed in exceptions.conf
            // and made fullscreen: the fallback kept naming the tiled window underneath, and the
            // border went on framing it while another app held the foreground. A border says which
            // window is ACTIVE, and when the active one is not tiled the honest answer is to draw
            // nothing.
            var foregroundHandle = foreground.GetForegroundHandle();

            // With no layout in force there is no tree to ask, so the border answers from the
            // WINDOW instead. Checked before the leaf lookup below rather than folded into it,
            // because that lookup is the very thing that cannot succeed in this mode.
            if (!tiling)
            {
                FrameTheForegroundWindow(foregroundHandle, live);
                return;
            }

            if (hook.IsPaused
                || foregroundHandle == 0
                || !registry.TryGetLeaf(foregroundHandle, out var focusedLeaf)
                || focusedLeaf is null
                || !registry.TryGetWindow(focusedLeaf.Window.Handle, out var focusedWindow)
                || focusedWindow is not { IsAlive: true })
            {
                RecordBorderDecision(
                    $"hidden: no framed window (paused={hook.IsPaused} foreground=0x{foregroundHandle:X})");
                focusBorder.Hide();
                return;
            }

            var onDisplay = treeManager.ResolveDisplay(focusedWindow.Bounds);
            if (!treeManager.TryGetTree(onDisplay, out var visibleTree)
                || visibleTree is null
                || !treeManager.TryGetTreeHolding(onDisplay, focusedLeaf, out var owningTree)
                || !ReferenceEquals(visibleTree, owningTree))
            {
                // GetForegroundWindow can keep naming the cloaked window from the desktop being
                // left during the shell's transition. The registry spans every desktop, so a valid
                // handle is not enough: only a leaf in the tree currently being viewed may be framed.
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is on another desktop's tree");
                focusBorder.Hide();
                return;
            }

            // The DISPLAY is still resolved from the settled bounds above, not from this. A window
            // dragged across a boundary has not changed which tree holds it -- that is decided at
            // the drop -- and asking mid-gesture would have the border answer a question the layout
            // has not answered yet.
            var framed = live ?? GestureBoundsFor(focusedWindow.Handle) ?? focusedWindow.Bounds;
            RecordBorderDecision(
                $"around 0x{foregroundHandle:X} [L={framed.Left} T={framed.Top} " +
                $"W={framed.Width} H={framed.Height}]");
            // Broken rather than solid for a window that will not take every size it is offered.
            // Its tile is not the whole story about where it actually sits -- it may overflow the
            // slot, or leave a gap inside it -- and a border identical to every other one would be
            // claiming a precision the layout does not have over it.
            focusBorder.ShowAround(
                focusedWindow.Handle, framed, onDisplay.Scaling, BorderGeometry.DefaultThickness,
                dashed: isConstrained(focusedWindow.Handle));
        }

        /// <summary>
        /// Frames whatever the user is looking at, for the mode where nothing is tiled.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reported the day the tiling switch landed: with tiling off, the windows on a second
        /// virtual desktop wore no border. It was never about that desktop -- the trace read
        /// "no framed window", not "is on another desktop's tree". The border only ever framed a
        /// LEAF, and with the layout off nothing new becomes one; the first desktop kept its
        /// borders only because its tree had been built before the switch was flipped.
        /// </para>
        /// <para>
        /// So the coupling is cut where it was never wanted. The border is its own switch and says
        /// which window is ACTIVE -- a fact about the desktop, not about the layout.
        /// </para>
        /// </remarks>
        void FrameTheForegroundWindow(nint foregroundHandle, Rectangle? live)
        {
            // Resolved through the WORKSPACE, not the registry: the registry holds tiled leaves,
            // which in this mode is precisely the set that is empty. The workspace tracks every
            // top-level window, which is what "the window in front of the user" means here.
            if (hook.IsPaused
                || foregroundHandle == 0
                || resolveAnyWindow(foregroundHandle) is not { IsAlive: true } window)
            {
                RecordBorderDecision(
                    $"hidden: nothing to frame without a tree " +
                    $"(paused={hook.IsPaused} foreground=0x{foregroundHandle:X})");
                focusBorder!.Hide();
                return;
            }

            // The tree used to answer "is this window on the desktop in view", and it was doing real
            // work: GetForegroundWindow keeps naming the cloaked window from the desktop being left
            // during the shell's transition. With no tree, the shell is asked directly.
            //
            // Guid.Empty is the shell DECLINING to say, which it answers for any window merely
            // mid-creation. Reading that as "somewhere else" is a mistake this repository has
            // already paid for once -- every arriving window was filed under a desktop nobody was
            // looking at, and tiling stopped outright -- so it is read as "here" exactly as the
            // arrival path reads it.
            var named = resolveWindowDesktop?.Invoke(foregroundHandle) ?? Guid.Empty;
            if (named != Guid.Empty && named != lastDesktop)
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is on another desktop");
                focusBorder!.Hide();
                return;
            }

            // Reported the same day: with tiling off the TASKBAR wore a border, and so did the
            // notification-area flyout -- clicking either makes it the foreground window, and this
            // path frames whatever the foreground is.
            //
            // Interop's trackability gate is no help and never claimed to be: Shell_TrayWnd is
            // visible, unowned, uncloaked and not a child, so it clears that gate outright and sits
            // in the snapshot like any application window. WS_EX_TOOLWINDOW is what separates
            // chrome from a window, and IsAutoExcluded is where that verdict already lives -- the
            // very same one that keeps the taskbar out of the TREE.
            //
            // Which is the point: with tiling ON, chrome went unframed only because it could never
            // become a leaf. That was the tree answering a question about the LAYOUT and getting a
            // question about the DESKTOP right by accident. With no tree to lean on, the border
            // asks directly, and now both modes answer from the same rule.
            //
            // AUTO exclusions only, not the user's list. A listed app is one the user asked not to
            // TILE; it is still a window they can be looking at, and the border says which window
            // is active.
            if (WindowFilters.IsAutoExcluded(WindowDescriptorBuilder.Build(window)))
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} is shell chrome, not a window");
                focusBorder!.Hide();
                return;
            }

            var framed = live ?? GestureBoundsFor(window.Handle) ?? window.Bounds;
            if (framed.Width <= 0 || framed.Height <= 0)
            {
                RecordBorderDecision($"hidden: 0x{foregroundHandle:X} has no rectangle to frame");
                focusBorder!.Hide();
                return;
            }

            RecordBorderDecision(
                $"around 0x{foregroundHandle:X} untiled [L={framed.Left} T={framed.Top} " +
                $"W={framed.Width} H={framed.Height}]");

            // SOLID, always. The broken border means "a size this window refuses BITES the tile it
            // is holding right now", and a window holding no tile makes no such claim -- drawing it
            // dashed here would drift the mark back to "this window once refused a size", which is
            // exactly what it was corrected away from.
            focusBorder!.ShowAround(
                window.Handle, framed, treeManager.ResolveDisplay(framed).Scaling,
                BorderGeometry.DefaultThickness, dashed: false);
        }

        // Unfiltered on purpose, unlike AfterArrange: a chord can change WHICH window is focused
        // without moving anything at all, and the border still has to go and find it.
        // Two things after every chord, in this order. A chord that reshapes the layout reaches the
        // adapter through no event at all -- a compliant window moving writes its own new bounds
        // into the cache, so the reconciliation pass compares the rectangle against itself and
        // reports nothing -- which is the same reason the border below cannot wait for one either.
        // Parked windows are asked FIRST so the border is drawn on the layout that results.
        executor.AfterAction = () =>
        {
            sessionAdapter.RetryParkedWindows();
            onOwningThread(UpdateFocusBorder);
        };

        // Following the window itself, not just the chord that moved it. An application may take
        // several frames to settle where it was put -- Windows Terminal animates its resize -- so a
        // single refresh after the chord lands before the window has finished arriving, and the
        // border visibly trails it. This arrives once per frame of that movement, on the hook's own
        // thread, and costs one placement each.
        // WHICH window the border is on, and asking the wrong way is not a near-miss: with tiling
        // off there is no focused leaf at all, so a leaf-shaped question answers "none" for every
        // window on the desktop and the border stops following anything. Dragging is the movement
        // that mode exists to allow, so it is where trailing by a tick would be most visible.
        nint FramedHandle() => tiling
            ? executor.ResolveFocusedLeaf()?.Window.Handle ?? 0
            : foreground.GetForegroundHandle();

        EventHandler<WindowEventArgs>? followFocusedWindow = null;
        EventHandler<WindowBoundsChangingEventArgs>? followDraggedWindow = null;
        EventHandler<WindowEventArgs>? forgetGestureOnRemoval = null;
        if (focusBorder is not null)
        {
            followFocusedWindow = (_, e) =>
            {
                // The settled report is the drop, and it carries the caught-up bounds -- so
                // whatever was being held over for this window has served its purpose. Done before
                // the framed check, because a window dragged while ANOTHER holds focus still ends
                // its own gesture here.
                ForgetGesture(e.Window.Handle);

                // Only the window being framed. During a reflow every tile reports a move, and
                // redrawing the border for windows it is not on is work with nothing to show for it.
                var framedHandle = FramedHandle();

                if (framedHandle != 0 && framedHandle == e.Window.Handle)
                {
                    UpdateFocusBorder();
                }
            };

            workspace.WindowBoundsChanged += followFocusedWindow;

            // The other half, and without it the border sat frozen for the length of a hand-resize.
            // WindowBoundsChanged is withheld for every frame between MOVESIZESTART and MOVESIZEEND
            // -- deliberately, and it stays that way: the layout answers a gesture ONCE, at the
            // drop, and the alternative was measured as a tiled window flickering against its own
            // snap-back dozens of times a second. The border moves nothing, so it may follow.
            //
            // The rectangle comes off the EVENT rather than off the window, because the window's
            // own cached bounds are a gesture behind on purpose.
            followDraggedWindow = (_, e) =>
            {
                gestureHandle = e.Window.Handle;
                gestureBounds = e.Bounds;

                var framedHandle = FramedHandle();

                if (framedHandle != 0 && framedHandle == e.Window.Handle)
                {
                    DrawFocusBorder(e.Bounds);
                }
            };

            workspace.WindowBoundsChanging += followDraggedWindow;

            // One handler, two jobs, one event -- and both are about a window that has just ceased
            // to exist.
            forgetGestureOnRemoval = (_, e) =>
            {
                // The one gesture that never reaches the drop above: the window is destroyed while
                // the user is still holding it. Windows reuses handles, so leaving the rectangle
                // behind would frame the next window to take this one at a size it never had.
                ForgetGesture(e.Window.Handle);

                // Reported from real use, and measured on hardware at 249ms: a closing window kept
                // its border for a moment after it was gone. A close used to reach the border only
                // through the ARRANGE pass -- remove, reflow the survivors, refresh -- and with
                // tiling off there is no reflow at all, so nothing but the tick ever noticed.
                //
                // Unconditional rather than filtered to the framed window, and that is the cheaper
                // reading as well as the honest one. The handle CANNOT be compared: by the time
                // this arrives the foreground already names something else, so "is this the window
                // the border is on" answers no for the very window that just closed. Windows close
                // rarely -- there is no storm here to guard against, unlike a bounds change.
                //
                // Called directly rather than marshalled, exactly as followFocusedWindow is: this
                // arrives on the hook's thread, and queuing it would put the tick's own latency
                // back in front of the fix.
                UpdateFocusBorder();
            };

            workspace.WindowRemoved += forgetGestureOnRemoval;
        }

        var watchTick = 0;

        // The tick drives the same tiling and focus work a CHORD does -- workspace polling, the
        // arrange pass, the arrival handover, a WPF redraw -- and a chord's throw is caught, one
        // action at a time, by ActionDispatcher. This one was not. It runs on a DispatcherTimer, so
        // anything it raised was an unhandled exception on the WPF UI thread: the whole process,
        // not one dropped chord. The asymmetry is the argument -- the two entry points reach the
        // same executor and the same TreeManager, and only one of them was allowed to fail.
        //
        // Recorded rather than swallowed, for the reason written on FileDesktopTrace: this
        // repository has twice paid for failures that left no trace. A tick that quietly does
        // nothing is indistinguishable, from outside, from one that worked.
        var reconcile = scheduleReconcile(WatchInterval, () =>
        {
            try
            {
                ReconcileOnce();
            }
            catch (Exception error)
            {
                desktopTrace?.Record($"tick-failed {error.GetType().Name}: {error.Message}");
            }
        });

        void ReconcileOnce()
        {
            if (++watchTick >= PollEveryNthWatch)
            {
                watchTick = 0;
                workspace.Poll();
            }

            // The taskbar hiding, moving to another edge or being resized changes the work area
            // and nothing else: no window moves, so nothing we subscribe to fires. Asking is the
            // only way to notice, and it goes BEFORE the desktop handling below so an arriving
            // layout is computed on the geometry that is true now.
            if (refreshDisplays is not null)
            {
                foreach (var changed in refreshDisplays())
                {
                    if (!displaysAwaitingReflow.Contains(changed))
                    {
                        displaysAwaitingReflow.Add(changed);
                    }

                    var area = changed.WorkArea;
                    desktopTrace?.Record(
                        $"work area changed on 0x{changed.Handle:X}: " +
                        $"{area.Left},{area.Top} {area.Width}x{area.Height}");

                    // T4: the mini window hugs a corner of the PRIMARY work area, so a taskbar that
                    // moved or resized re-places it. Independent of the tiling freeze below: this
                    // window is not part of any layout. Already on the owning thread (watch tick).
                    if (miniMode && changed.Equals(treeManager.Primary))
                    {
                        ReplaceMiniWindow();
                    }
                }

                if (displaysAwaitingReflow.Count > 0 && !LayoutIsFrozen())
                {
                    // Emptied BEFORE the reflow, so a display that throws costs one failed tick
                    // (recorded by the catch around this one) instead of a retry every interval.
                    var due = displaysAwaitingReflow.ToArray();
                    displaysAwaitingReflow.Clear();

                    foreach (var display in due)
                    {
                        treeManager.OnDisplayChanged(display, WorkAreaResolver.Resolve(display));
                    }
                }
            }

            // Publishes off the hook thread, so the hook itself never waits on a file.
            if (hook.LastUnmatchedChord is { } unmatched && unmatched != lastReportedUnmatched)
            {
                lastReportedUnmatched = unmatched;
                desktopTrace?.Record($"unmatched chord: {unmatched}");
            }

            // Published here for the same reason as the unmatched-chord line above: a hook that
            // touches a file is a hook Windows uninstalls, so the count is only ever written off the
            // hook thread, from this tick.
            //
            // This is the hole in the instrument the seven days of trace evidence pointed at: a
            // chord that MATCHED can still be discarded silently. TryWrite on a full channel with
            // FullMode.Wait does not block -- it returns false -- and RecordUnmatched above never
            // fires for it, because the chord matched. Without this line, that dead stretch wrote
            // NOTHING at all.
            var dropped = hook.DroppedChords;
            if (dropped != lastReportedDropped)
            {
                desktopTrace?.Record(
                    $"chord dropped -- queue full: total={dropped} " +
                    $"(+{dropped - lastReportedDropped} since last report) last={hook.LastDroppedChord}");
                lastReportedDropped = dropped;
            }

            // Windows silently uninstalls a low-level keyboard hook whose callback overruns
            // LowLevelHooksTimeout. A ghosted hook delivers nothing, so the watchdog puts it back a
            // few seconds later and the keyboard "comes good on its own" -- which is what a dead
            // stretch of chords looks like from the user's side, and it used to leave no record at
            // all. Without this line a stretch caused by a ghosted hook and one caused by a lost
            // modifier read identically in the trace.
            //
            // `foundGone` is the half that decides whether the watchdog is the cure or the disease.
            // Its trigger is silence, not death, and a keyboard is silent nearly all the time -- so
            // a total that climbs while this stays at zero says every one of those reinstalls tore
            // down a hook Windows was still holding, and opened a gap for nothing.
            var reinstalls = hook.WatchdogReinstalls;
            if (reinstalls != lastReportedReinstalls)
            {
                desktopTrace?.Record(
                    $"hook reinstalled by watchdog -- total={reinstalls} " +
                    $"(+{reinstalls - lastReportedReinstalls} since last report) " +
                    $"foundGone={hook.WatchdogFoundHookGone}");
                lastReportedReinstalls = reinstalls;
            }

            if (virtualDesktops is not null)
            {
                // Windows moves windows on its own -- closing a desktop hands its windows to
                // another, and Task View can drag one across. Neither raises anything we listen to,
                // so the only way to notice is to ask.
                sessionAdapter.ReconcileDesktops();

                var nowOn = virtualDesktops.CurrentDesktopId;
                if (nowOn != lastDesktop)
                {
                    lastDesktop = nowOn;
                    lastDesktopIndex = virtualDesktops.CurrentIndex;
                    ApplyArrivingLayout();

                    // The switch chord answers itself; this is the same handover for the switches
                    // CosmicWin did not make -- Win+Ctrl+arrow, Task View -- which raise nothing we
                    // subscribe to. AFTER the layout, so the tree it searches is the arriving one.
                    executor.HandFocusToArrivingDesktop();
                }
            }

            // Keeps the executor's focus record within one interval of the real foreground. Without
            // it the record only advances on a chord, so a user who clicks between windows with the
            // mouse and then opens a new one would have it split whichever tile they last used a
            // hotkey on (LE-4 placement). One native read, so it belongs on the cheap tick.
            executor.ResolveFocusedLeaf();

            // Files the foreground under the desktop in view, every pass. The switch CHORD records
            // this itself, precisely and race-free; this is for the switches CosmicWin does not
            // make -- Win+Ctrl+arrow, Task View -- where by the time the block above notices, the
            // current desktop id already names the ARRIVING desktop and it is far too late to
            // record where the user was.
            //
            // AFTER the handover above on purpose. On a tick that detects an arrival the foreground
            // has just been handed on, so what gets filed is the arriving desktop's own window --
            // which is true. Noted BEFORE it, the departing desktop's foreground would be filed
            // under the arriving desktop's key: the record would be not merely stale but wrong.
            executor.NoteFocusOnCurrentDesktop();

            // T3 (video-wallpaper-repick-and-slideshow): re-raise the video wallpaper host above a
            // slideshow-created wallpaper layer. Win32VideoWallpaperHost.AttachToDesktop's own fast
            // path now re-applies the z-order (and only the z-order) when Explorer's slideshow has
            // inserted a fresh WorkerW directly after DefView, above the host -- see that class's
            // remarks and T2's proven cause. Nothing else calls TryAttach on this cadence, so this
            // tick is what actually notices; gated on videoWallpaperActive so a never-activated or
            // failed video wallpaper posts nothing, and on the pending flag so a slow
            // video-wallpaper thread never gets a second one queued behind the one it has not run.
            //
            // D3 (html-wallpaper-demo): the SAME re-raise applies verbatim to html mode's own
            // composition swapchain -- htmlWallpaperActive is that mode's equivalent of
            // videoWallpaperActive, so this tick must also fire while the host is attached in html
            // mode, not only while a video is genuinely playing.
            if ((videoWallpaperActive.Value || htmlWallpaperActive.Value)
                && videoWallpaperHost is not null && !videoWallpaperKeepAlivePending.Value)
            {
                videoWallpaperKeepAlivePending.Value = true;
                onVideoWallpaperThread(() =>
                {
                    videoWallpaperKeepAlivePending.Value = false;
                    // A transient attach failure must not disable the next watch tick's retry.
                    // Activation/Stop own the playback flag; keep-alive only restores attachment.
                    videoWallpaperHost.TryAttach();
                });
            }

            UpdateAlertOverlay();
            UpdateFocusBorder();
        }

        // A separate path on purpose, sharing only the pause flag. Modal dialogs never reach the
        // workspace above -- its trackability gate drops every owned window, which is what keeps
        // tooltips and context menus out of the tiling engine -- so seeing them at all takes a
        // second, narrower event source that touches none of it.
        FloatingDialogAdapter? dialogAdapter = null;
        if (windowShown is not null)
        {
            dialogAdapter = new FloatingDialogAdapter(
                windowShown, treeManager, () => exceptionStore.Current, LayoutIsFrozen)
            {
                // Off unless asked for: one line per owned window shown anywhere on the desktop is
                // diagnostic volume, not something to write during ordinary use.
                //
                // A marker FILE, not an environment variable. CosmicWin always runs elevated, and an
                // elevated process launched through UAC gets a fresh environment block rather than
                // its launcher's -- so a variable set beside the launch would silently never arrive,
                // and the diagnostic would look like it had proven the path dead.
                Trace = File.Exists(TraceMarkerPath) ? desktopTrace : null,
            };
            // The other half of the executor's untracked-foreground rule: a move chord aimed at a
            // window the tree does not contain is offered here instead of being dropped.
            executor.MoveFloatingWindow = dialogAdapter.TrySnap;
            windowShown.Open();
        }

        // T6 startup activation: attach and start playback once, but only when there is already a
        // path configured -- the user's FIRST-ever pick has no path here yet and is instead handled
        // entirely by the setVideoWallpaperPath closure above. On the thread whose message loop will
        // pump the host window: Win32VideoWallpaperHost's own doc comment requires this (its
        // TaskbarCreated re-attach depends on being pumped), and onOwningThread is already how every
        // other Win32-window-touching callback in this method reaches that thread.
        //
        // D3 (html-wallpaper-demo): the SAME threading rule applies in html mode, but the activation
        // itself is attach-only (see AttachHtmlWallpaper) -- no path is required, and a configured one
        // is deliberately ignored (never played) rather than left to a stale ActivateVideoWallpaper
        // call, since the demo's whole point is that an animated HTML scene page is the wallpaper.
        //
        // T4 (mini-scene-window): mini mode instead builds NOTHING wallpaper-shaped. The corner window
        // is shown on the owning UI thread (its owner; WebView2 needs the dispatcher pumping, so it
        // is posted rather than run inline during wiring) and the video branch below is never
        // reached -- even if a host/player were handed in and a video path is configured.
        if (miniMode)
        {
            onOwningThread(StartMiniWindow);
        }
        else if (videoWallpaperHost is not null && videoWallpaperPlayer is not null)
        {
            if (wallpaperMode == WallpaperMode.Html)
            {
                onVideoWallpaperThread(() => AttachHtmlWallpaper("startup"));
            }
            else if (videoWallpaperPath is not null)
            {
                onVideoWallpaperThread(() => ActivateVideoWallpaper("startup", videoWallpaperPath));
            }
        }

        return new AppComposition(
            dispatcher, hook, workspace, sessionAdapter, tray, reconcile, windowShown, dialogAdapter,
            focusBorder, videoWallpaperHost, videoWallpaperPlayer, () =>
            {
                alertServer?.Dispose();
                httpAlertServer?.Dispose();
                alertLayer?.Dispose();
                miniWindow?.Dispose();
                disposeVideoWallpaperBase();
            },
            unfollowFocusedWindow: () =>
            {
                if (followFocusedWindow is not null)
                {
                    workspace.WindowBoundsChanged -= followFocusedWindow;
                }

                if (followDraggedWindow is not null)
                {
                    workspace.WindowBoundsChanging -= followDraggedWindow;
                }

                if (forgetGestureOnRemoval is not null)
                {
                    workspace.WindowRemoved -= forgetGestureOnRemoval;
                }
            });
    }

    /// <summary>The sole production caller of <see cref="Wire"/>: supplies the real Win32 collaborators, including a real <see cref="Win32DisplayManager"/>-backed <see cref="TreeManager"/>. Called exactly once, from <c>App.OnStartup</c>.</summary>
    public static AppComposition WireProduction(Action shutdown)
    {
        var registry = new WindowRegistry();
        var foreground = new Win32ForegroundWindowSource();
        var displayManager = new Win32DisplayManager();
        var treeManager = new TreeManager(displayManager.Displays, displayManager.Primary, registry);
        var exceptionStore = new ExceptionListStore(ExceptionListFile.Load());
        var workspace = new Win32Workspace();

        var desktops = new Win32VirtualDesktopService();

        // S10 (wallpaper-scene-http-endpoint, R3-first-run-write-failure-silent): desktopTrace now
        // constructed HERE, ahead of the settings load below, instead of after it (T9a originally put
        // it right before the alert layer, further down) -- so a failed first-run settings.conf write
        // has somewhere to report to. Moving it earlier costs nothing: FileDesktopTrace's constructor
        // only resolves a path, it opens no file and depends on no other collaborator built below.
        var desktopTrace = new FileDesktopTrace(FileDesktopTrace.ResolveDefaultPath());

        // S10: reports a failed settings write -- first-run create below, OR any later toggle through
        // settingsStore's own save delegate -- by exception TYPE NAME ONLY (never the path or
        // message), the same house rule every other `*-failed` line in this file already follows
        // (e.g. SwitchVideoWallpaper's own import-failed line). SettingsFile.Save/LoadOrCreate still
        // swallow the failure and keep running on the in-memory value; this only makes that swallow
        // observable instead of silent.
        void OnSettingsSaveFailed(string errorType) =>
            desktopTrace.Record($"settings-file save-failed error={errorType}");

        // Read ONCE here rather than inside Wire, so every test drives the same composition with the
        // value stated explicitly instead of whatever this machine's file happens to say. Moved
        // ahead of the gap assignment below (T5, alert-tile-mosaic) so TreeArranger.Gap can start
        // from the settings file's own gap key instead of always starting from the compiled-in
        // default and waiting for a Reload to correct it.
        //
        // S9 (wallpaper-scene-http-endpoint, 2026-09-27): LoadOrCreate rather than Load -- the ONE
        // production call site that must also WRITE settings.conf when it is missing, since CosmicWin
        // has no installer and first start is the install moment. SettingsFile.Load itself stays
        // side-effect-free for its other callers (loadGap's Reload below, tests).
        var settings = SettingsFile.LoadOrCreate(onDiagnostic: OnSettingsSaveFailed);

        // Spacing is a production choice, not a property of the tiling arithmetic -- the engine and
        // every geometry fact in the suite work in exact, gapless rectangles. Opting in here keeps
        // the knob in one visible place instead of baked into TreeArranger's default. T5
        // (alert-tile-mosaic): now the settings file's own value, which itself defaults to
        // TreeArranger.DefaultGap when the `gap` key is absent or unreadable (Settings.Parse).
        TreeArranger.Gap = settings.Gap;

        // The file carries MORE THAN ONE setting now, so each save has to start from the whole
        // record. Rebuilding it from the one value that changed -- which is what
        // `new Settings(FocusBorder: enabled)` did -- would write the colour back to the accent
        // every time somebody toggled the border, and vice versa.
        //
        // S6 (wallpaper-scene-http-endpoint, R3-persist-shared-stored-capture): this used to be a
        // bare mutable local, and every persistXyz closure below did its own unsynchronized
        // `SettingsFile.Save(stored = stored with { ... })`. Focus-border/border-colour/tiling/scene
        // run on the UI STA thread; the video path persists from the video-wallpaper MTA thread
        // (`SwitchVideoWallpaper`'s posted work item) -- two of those closures firing concurrently
        // could both read the SAME pre-update snapshot and race their `with`, one silently clobbering
        // the other's field. SynchronizedSettingsStore.Update wraps the whole read-modify-write-and-
        // save in one lock, so every persist below is now serialized against every other one.
        //
        // S10: the save delegate now reports a failed write through the SAME OnSettingsSaveFailed
        // the first-run LoadOrCreate above uses, so every persistXyz closure below (focus border,
        // border colour, tiling, video path, scene) gets the same observability, not just first run.
        var settingsStore = new SynchronizedSettingsStore(
            settings, s => SettingsFile.Save(s, OnSettingsSaveFailed));

        // ONE hoisted instance for the life of the process, read once per untiled focus chord --
        // not reconstructed per chord, which would pay Win32NativeWindowSource's own construction
        // cost on every keypress for no benefit, since it carries no per-call state to keep fresh.
        var zOrderSource = new Win32ZOrderSource();

        // T4 (mini-scene-window): mini mode is not a wallpaper at all -- no host, no player, no video
        // thread and no wallpaper alert layer. The corner window below takes their place.
        var miniMode = settings.WallpaperMode == WallpaperMode.HtmlMini;
        Win32VideoWallpaperHost? videoWallpaperHost = miniMode ? null : new Win32VideoWallpaperHost();
        MediaFoundationVideoWallpaperPlayer? videoWallpaperPlayer = miniMode ? null : new MediaFoundationVideoWallpaperPlayer();
        // Built here, on the owning UI STA (the controller records its thread and every later call must
        // come from it); shown later, on the pumped dispatcher, by Wire. Null in every other mode.
        var miniWindow = miniMode ? MiniSceneWindowController.CreateProduction(desktopTrace.Record) : null;
        // T9a (webview-alert-layer): desktopTrace (constructed further up now, S10) is passed here so
        // BOTH the controller's own lifecycle telemetry and Wire's desktopTrace parameter share the
        // exact same sink -- T6 found production had no alert-layer navigation/render telemetry at
        // all, which left F1/F2 unexplained.
        // Startup runs on the owning STA before its dispatcher synchronization context may
        // be installed. WebView2 creation is deferred until the pumped reconciliation tick.
        var alertLayer = settings.AlertsEnabled && !miniMode
            ? new WebViewAlertLayerController(videoWallpaperHost!, trace: desktopTrace.Record,
                // D3 (html-wallpaper-demo): navigates to the configured scene page and stays visible
                // permanently once ready, instead of the ordinary alert-only page.
                htmlWallpaperMode: settings.WallpaperMode == WallpaperMode.Html,
                // D6d (html-wallpaper-demo): which scene and frame-rate cap -- irrelevant in video
                // mode, where the controller never reads either field.
                htmlWallpaperScene: settings.WallpaperScene,
                htmlWallpaperFps: settings.WallpaperFps)
            : null;
        // desktopTrace already exists above (created ahead of the alert layer for T9a), so the video
        // wallpaper thread's failure sink can point at it directly with no reordering.
        var videoWallpaperThread = miniMode ? null : new MtaActionThread(
            "CosmicWinVideoWallpaperHost",
            onWorkFailed: errorType => desktopTrace.Record($"video-wallpaper-thread work-failed error={errorType}"));

        return Wire(
            workspace, treeManager, registry, foreground, exceptionStore,
            focusTrace: new FileFocusTrace(FileFocusTrace.ResolveDefaultPath()),
            disableTaskTrigger: DisableScheduledTaskTrigger,
            scheduleReconcile: ScheduleOnUiThread,
            hookFactory: writer => new LowLevelKeyboardHook(writer),
            loadExceptions: ExceptionListFile.Load,
            // T5 (alert-tile-mosaic): mirrors loadExceptions -- a FRESH read on every Reload, not
            // the one-time `settings` value captured above, so hand-editing settings.conf's `gap`
            // key and clicking Reload behaves exactly the way editing exceptions.conf already does.
            loadGap: () => SettingsFile.Load().Gap,
            shutdown: shutdown,
            buildTray: controller => new TrayIconHost(controller),
            importVideoWallpaper: VideoWallpaperImport.Import,
            // Gated internally: an unrecognised Windows build reports unsupported and the desktop
            // chords become inert, rather than calling through a vtable that may have moved.
            virtualDesktops: desktops,
            desktopTrace: desktopTrace,
            resolveWindowDesktop: desktops.ResolveWindowDesktop,
            windowShown: new Win32WindowShownWatcher(),
            focusBorder: new FocusBorderOverlay(),
            scheduleOnOwningThread: RunOnUiThread,
            focusBorderEnabled: settings.FocusBorder,
            persistFocusBorder: enabled => settingsStore.Update(s => s with { FocusBorder = enabled }),
            focusBorderColor: settings.BorderColor,
            persistBorderColor: rgb => settingsStore.Update(s => s with { BorderColor = rgb }),
            tilingEnabled: settings.Tiling,
            persistTiling: enabled => settingsStore.Update(s => s with { Tiling = enabled }),
            persistVideoWallpaperPath: path => settingsStore.Update(s => s with { VideoWallpaperPath = path }),
            readVideoFileSnapshot: VideoWallpaperImport.TryReadSnapshot,
            alertsEnabled: settings.AlertsEnabled,
            httpServerEnabled: settings.HttpServerEnabled,
            httpServerPort: settings.HttpServerPort,
            // S4 (wallpaper-scene-http-endpoint): null (no live switch to offer) when no alert layer
            // exists, exactly the same shape startAlertLayer/endAlertLayer/preloadAlertLayer below
            // already use for the SAME alertLayer collaborator.
            switchHtmlWallpaperScene: alertLayer is null ? null : alertLayer.SwitchScene,
            persistWallpaperScene: scene => settingsStore.Update(s => s with { WallpaperScene = scene }),
            startAlertLayer: alertLayer is null ? null : alertLayer.Start,
            endAlertLayer: alertLayer is null ? null : alertLayer.End,
            shakeAlertVideo: videoWallpaperPlayer is null ? null : duration => videoWallpaperPlayer.Shake(duration),
            alertLayer: alertLayer,
            alertRendererReady: videoWallpaperHost is null ? null : () => videoWallpaperHost.IsCompositionReady,
            preloadAlertLayer: alertLayer is null ? null : alertLayer.Preload,
            // T10 (live-alert-wallpaper): the real covered-desktop signal T9 found missing --
            // without it an alert played out unseen under a fullscreen video or browser instead of
            // being held. Reused, not re-invented: PrimaryMonitorFullscreenDetector applies the SAME
            // fullscreen definition a023fac proved for the tiling engine.
            isPrimaryMonitorCovered: PrimaryMonitorFullscreenDetector.IsPrimaryMonitorCoveredByFullscreenWindow,
            // Constructed unconditionally, mirroring windowShown: new Win32WindowShownWatcher()
            // above. Construction alone attaches/plays nothing -- only TryAttach/TryPlay do, gated
            // in Wire by videoWallpaperPath being non-null (startup) or the tray pick itself.
            // The attach/play work runs on a dedicated MTA thread, not the WPF Dispatcher STA:
            // IMFMediaEngine frame-server setup fails when the host D3D11 device is created on STA.
            videoWallpaperHost: videoWallpaperHost,
            videoWallpaperPlayer: videoWallpaperPlayer,
            scheduleVideoWallpaperWork: videoWallpaperThread is null ? null : videoWallpaperThread.Post,
            disposeVideoWallpaper: () =>
            {
                if (videoWallpaperThread is null)
                {
                    return;
                }

                videoWallpaperThread.Invoke(() =>
                {
                    videoWallpaperPlayer?.Dispose();
                    videoWallpaperHost?.Dispose();
                });
                videoWallpaperThread.Dispose();
            },
            videoWallpaperPath: settings.VideoWallpaperPath,
            wallpaperMode: settings.WallpaperMode,
            miniWindow: miniWindow,
            wallpaperScene: settings.WallpaperScene,
            wallpaperFps: settings.WallpaperFps,
            miniPosition: settings.MiniPosition,
            persistMiniPosition: corner => settingsStore.Update(s => s with { MiniPosition = corner }),
            zOrder: zOrderSource.EnumerateTopLevelWindows,
            refreshDisplays: displayManager.Refresh);
    }

    /// <summary>
    /// Marshals work onto the WPF UI thread, which owns the overlay window and the WinEvent hook.
    /// </summary>
    /// <remarks>
    /// A chord is answered on <see cref="ActionDispatcher.RunAsync"/>'s pool thread, and a WPF
    /// window may only be touched by the thread that created it. Without this, the very refresh that
    /// makes the border keep up would throw from another thread instead.
    /// </remarks>
    private static void RunOnUiThread(Action work) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(work);

    /// <summary>
    /// Runs the reconciliation pass on the WPF UI thread -- the SAME thread that installs the
    /// WinEvent hook and therefore receives its callbacks. Both mutate the same trees and registry,
    /// so a pool-thread timer would race them; a <see cref="DispatcherTimer"/> serialises the two by
    /// construction.
    /// </summary>
    private static IDisposable ScheduleOnUiThread(TimeSpan interval, Action callback)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => callback();
        timer.Start();
        return new TimerStopper(timer);
    }

    private sealed class TimerStopper(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private const string TaskName = "CosmicWin";

    /// <summary>Create this file to make the window paths trace themselves; delete it to stop.</summary>
    internal static string TraceMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CosmicWin",
        "trace-dialogs");

    /// <summary>
    /// The ONE delegating call <c>App.OnStartup</c> makes for
    /// <c>--install-task</c>/<c>--uninstall-task</c>, called BEFORE <see cref="WireProduction"/>.
    /// <paramref name="runner"/> defaults to the real runner, overridable only for tests.
    /// </summary>
    public static bool TryHandleTaskCommand(IReadOnlyList<string> args, IProcessRunner? runner = null)
    {
        if (args.Count == 0)
        {
            return false;
        }

        var installer = CreateInstaller(runner);

        switch (args[0])
        {
            case "--install-task":
                installer.Install();
                return true;
            case "--uninstall-task":
                installer.Uninstall();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The one place the real <see cref="TaskInstaller"/> is constructed, shared by <see
    /// cref="TryHandleTaskCommand"/> and <see cref="DisableScheduledTaskTrigger"/> so the task name,
    /// executable path and XML location cannot drift apart between the two.
    /// </summary>
    private static TaskInstaller CreateInstaller(IProcessRunner? runner = null) =>
        new(
            TaskName,
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the current process path."),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CosmicWin", "CosmicWinTask.xml"),
            runner ?? new Win32ProcessRunner());

    /// <summary>
    /// TC-3-W1's production trigger-disable. A failure here is DELIBERATELY swallowed: the user asked
    /// to quit, and Salir's own contract -- remove the hook and exit -- must not be held hostage by
    /// schtasks. <see cref="TaskInstaller.Disable"/> already treats an absent task as success, so
    /// what reaches this catch is a genuine refusal (no elevation, service stopped), not routine.
    /// </summary>
    private static void DisableScheduledTaskTrigger()
    {
        try
        {
            CreateInstaller().Disable();
        }
        catch (InvalidOperationException)
        {
            // Declared residual: quitting still works, but the trigger may fire again next logon.
        }
    }

    /// <summary>
    /// Made <c>internal</c> (rather than extracted to its own file) so <c>CosmicWin.App.Tests</c> --
    /// already granted access via <see cref="System.Runtime.CompilerServices.InternalsVisibleToAttribute"/>
    /// in <c>AssemblyInfo.cs</c> -- can drive <see cref="Run"/>'s failure path directly. That path
    /// used to swallow a posted work item's exception with no trace at all; <paramref name="onWorkFailed"/>
    /// (see the constructor) is how a failure now reaches the desktop trace instead.
    /// </summary>
    internal sealed class MtaActionThread : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;
        private readonly Action<string>? _onWorkFailed;
        private bool _disposed;

        /// <param name="onWorkFailed">
        /// Invoked with the exception TYPE name only (never <see cref="Exception.Message"/>, which can
        /// hold an absolute path) when posted work throws. The loop keeps serving later work either way.
        /// </param>
        public MtaActionThread(string name, Action<string>? onWorkFailed = null)
        {
            _onWorkFailed = onWorkFailed;
            _thread = new Thread(Run) { IsBackground = true, Name = name };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Post(Action work) => _ = TryPost(work);

        public void Invoke(Action work)
        {
            if (Thread.CurrentThread == _thread)
            {
                work();
                return;
            }

            var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!TryPost(() =>
            {
                try
                {
                    work();
                    completed.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completed.TrySetResult(exception);
                }
            }))
            {
                return;
            }

            if (!completed.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                return;
            }

            if (completed.Task.Result is { } failure)
            {
                throw new InvalidOperationException("Video wallpaper thread work failed.", failure);
            }
        }

        private bool TryPost(Action work)
        {
            if (_disposed)
            {
                return false;
            }

            try
            {
                _queue.Add(work);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            if (_thread.Join(TimeSpan.FromSeconds(5)))
            {
                _queue.Dispose();
            }
        }

        private void Run()
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var work, millisecondsTimeout: 16))
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        // Posted wallpaper work must not kill the thread that owns the host HWND.
                        // Synchronous Invoke callers wrap their own failures before they get here.
                        // Only the TYPE reaches the sink: exception.Message can hold an absolute path.
                        // Review R3-002: the sink is guarded too, so "keeps serving later work"
                        // never depends on it -- an exception escaping here would end this thread,
                        // and, unhandled on a background thread, the whole process.
                        try
                        {
                            _onWorkFailed?.Invoke(exception.GetType().Name);
                        }
                        catch
                        {
                        }
                    }
                }

                PumpThreadMessages();
            }
        }

        private static void PumpThreadMessages()
        {
            while (PInvoke.PeekMessage(out MSG message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
    }

    /// <summary>Mirrors <c>App.OnExit</c>'s exact disposal order, after stopping WT-1's reconciliation pass: tray, hook, adapter, workspace, dispatcher.</summary>
    public void Dispose()
    {
        // Stopped FIRST: a pass that fires mid-teardown would reconcile against a disposed workspace.
        _reconcile.Dispose();
        _tray.Dispose();
        _hook.Dispose();

        // The adapter first, then the source it listens to: unsubscribing before the hook is torn
        // down means no event can arrive against a half-disposed adapter.
        // Unsubscribed before the workspace it listens to is torn down, so no late event can arrive
        // against a disposed overlay.
        _unfollowFocusedWindow();
        _dialogAdapter?.Dispose();
        _windowShown?.Dispose();
        _focusBorder?.Dispose();

        // Player before host: the player's worker thread reads the host's D3D11 device and
        // swapchain on every tick (Win32VideoWallpaperHost.Device/GetBackBuffer), so stopping the
        // player first guarantees no tick can touch a host mid-teardown or already destroyed.
        // MediaFoundationVideoWallpaperPlayer.Dispose() joins that thread (bounded) before returning.
        _disposeVideoWallpaper();

        _sessionAdapter.Dispose();
        _workspace.Dispose();
        _dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
