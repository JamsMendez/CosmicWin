# WebView alert layer

## Objective

Show the great-sage warning / failed alert layer as the original HTML, rendered by a transparent
WebView2 over the video wallpaper. One layer at a time (warning or failed), covering the whole
primary display. For `failed`, the video wallpaper itself shakes first (as the page shakes its
canvases), then the failed layer appears.

## Why

Maintainer request, 2026-09-23: after the native Direct2D port (feature `live-alert-wallpaper`),
try the simpler route that reuses the page's own drawing code instead of a C# port.

## Scope

In scope: primary monitor; kinds `warning` / `failed`; reuse of the existing named pipe,
`CosmicWinAlert.exe` client, `AlertCommandParser`, `AlertQueue` and covered-desktop detector;
a trimmed, transparent copy of the page's alert layer (from
`\\wsl.localhost\FedoraLinux-42\home\jamsmendez\Documents\AI\great-sage\backgroud-processing\script.js`,
functions `drawFailureLayer` / `drawFailureOverlay` / `drawFailureTitle` /
`drawFailureBandIntersections` / `drawFailureModules` / `advanceFailureState`, themes
`FAILURE_OVERLAY_THEMES`); native video shake modelled on `applyFailureShake` (230 ms decaying
translate + rotate + scale 1.18).
Out of scope: tiles / per-count grids, multi-monitor, the page's background scene.

## Constraints and decisions

- **Dependency:** adds the `Microsoft.Web.WebView2` NuGet package and relies on the WebView2
  Runtime shipped with Windows 11. This overrides the earlier "no third-party dependencies" rule
  for this feature, at the maintainer's request.
- **Transparency:** a windowed WebView2 in a child window cannot show a sibling's pixels, so the
  plan is DirectComposition on the existing host HWND: swapchain visual (video) below, WebView2
  `CoreWebView2CompositionController` visual above, `DefaultBackgroundColor` transparent. T0
  proves or rejects this.
- **Idle cost (superseded 2026-09-24):** originally the WebView2 was created when an alert started
  and disposed when it ended. T6 measured ~2.8 s from command to visible layer with that design,
  plus F1 (first alert never shows). The maintainer then chose a **permanent preload**: one
  controller is created at startup, kept hidden, and shown/hidden per alert. Cost accepted:
  hundreds of MB of resident WebView2 memory. Condition: GPU while hidden must stay ~0 %
  (measured in T9e).
- **Offline:** the page loads Archivo Black from Google Fonts; the trimmed page must not need the
  network (bundle the font, SIL OFL, or fall back to Arial Black).
- **Combined commands (default, maintainer may override):** one layer at a time, so counts are
  ignored and `failed` wins over `warning` when a command names both.
- **Not in v1:** the page's backdrop pixelation for `failed` (a web page cannot read the video's
  pixels behind it; it could later be done natively in the player).
- Direct2D overlay from `live-alert-wallpaper` is switched off while this is tested (setting or
  wiring switch), not deleted.

## Delivery strategy

Maintainer chose `feature-branch-chain` for this >400-line feature on recovery. Keep coherent
behavior with its tests in each work unit; stage future dependent PR slices on the feature chain.
No PR or push is authorized by this choice. T4 alone currently has ~609 authored changed lines
before the task-document update; keep that coherent unit intact and disclose its review-size
exception if a single cohesive slice cannot fit the budget.

**2026-09-24 update:** the maintainer asked for local chained PRs stacked to `main`
(`stacked-to-main`, local branches only, no push, no remote PRs). This supersedes the
feature-branch-chain choice above for delivery. The branch carries three features
(explorer-restart-reattach, live-alert-wallpaper, webview-alert-layer): 81 commits, 64 files,
+10211/-51 against `main` (`fadfda7`). They were cut into 29 contiguous slices with no history
rewrite. `chain/NN-*` points at each slice's last commit, `chain/01` is based on `main`, and each
later branch is based on the one before it. Every slice tip was built and tested on its own in a
throwaway worktree: 29/29 `dotnet build` ok, `dotnet test CosmicWin.sln` 0 failures. Slices over
~400 lines are single cohesive commits (code with its tests) that cannot be split without
rewriting history, so they carry a `size:exception`: 01 (402), 07 (595), 13 (559), 14 (479),
15 (445), 19 (738), 20 (633), 21 (652), 28 (837). `8d5664f` (a docs-only review record that
exists only on `feat/live-alert-wallpaper`) is not in the chain.

| # | branch | lines | # | branch | lines |
|---|---|---|---|---|---|
| 01 | explorer-restart-reattach | 402 | 16 | host-whole-monitor | 170 |
| 02 | alert-wallpaper-plan | 273 | 17 | covered-desktop-hold | 398 |
| 03 | alert-command-parser | 380 | 18 | covered-desktop-shell-fix | 307 |
| 04 | alert-tile-layout | 349 | 19 | alert-web-page | 738 |
| 05 | alert-queue | 369 | 20 | dcomp-video-host | 633 |
| 06 | alert-pipe-protocol | 265 | 21 | native-video-shake | 652 |
| 07 | alert-pipe-server | 595 | 22 | lazy-webview-layer | 390 |
| 08 | alert-client | 372 | 23 | webview-queue-routing | 307 |
| 09 | alert-hardening | 305 | 24 | failed-timing-and-dcomp-race | 300 |
| 10 | alert-pipe-reply-bound | 358 | 25 | start-retry-and-test-followups | 275 |
| 11 | alert-client-timeouts | 312 | 26 | alert-layer-telemetry | 343 |
| 12 | frame-overlay-seam | 203 | 27 | alert-page-show-hide | 206 |
| 13 | direct2d-alert-overlay | 559 | 28 | preloaded-alert-layer | 837 |
| 14 | alert-overlay-visuals | 479 | 29 | immediate-tick-and-stale-ready | 220+ |
| 15 | live-alert-wiring | 445 | | | |

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj`. T0 is a throwaway hardware spike, manual.

## Tasks

- [x] **T0 — Spike (gate), throwaway branch `spike/webview-alert-t0`.** Switch the host's
  swapchain to composition (`CreateSwapChainForComposition` + DComp target on the host HWND),
  add a transparent WebView2 composition visual above it loading a stub page (translucent red
  band + text). Check on hardware: video visible through the transparent page, below the icons,
  survives the 400 ms re-attach and an Explorer restart; GPU/memory with the WebView2 alive vs
  disposed; create-to-first-paint latency. Route: delegated writer, hardware run by the parent.
  **Passed, 2026-09-23.** Spike commits `b77366a` (writer) and `26fa64b` (parent fixes) on
  `spike/webview-alert-t0`, toggle `COSMICWIN_SPIKE_WEBVIEW=1`. The first hardware run failed in
  two ways, and both were fixed in the spike:
  - The DComp RCWs created on the MTA video thread cannot be QI'd from the STA WPF thread
    (`InvalidCastException`, `E_NOINTERFACE`). Fix: the UI thread builds its own RCWs over the
    same raw pointers (`Marshal.GetUniqueObjectForIUnknown`). DirectComposition objects are
    free-threaded. `SpikeCommit` had the same bug, failed silently, and the page never showed.
  - Every failed attempt leaked a live controller (38 `msedgewebview2` processes). Fixed by
    closing the controller on each failure path.
  Evidence after the fixes (Debug, 3440x1440):
  - The transparent page (translucent red band + "SPIKE") draws over the live video, and the
    video shows through. Two captures 1 s apart differ in 1172 of 1200 sampled pixels outside the
    band, so the video is animating.
  - Timings: environment 0–8 ms, controller 244–300 ms, first `NavigationCompleted` 35–84 ms.
  - GPU 3D, 8 samples, windows minimized. Total system: spike off 6.69 % (dwm 5.18 %), spike on
    3.80 % (dwm 1.95 %; app 0.13 %, WebView procs 1.24 %). The composition swapchain is cheaper
    for DWM than the HWND swapchain. The stub page is trivial, so the real alert page will cost
    more.
  - Memory: 6 WebView2 processes, 339 MB working set / 206 MB private. The app goes 221 → 277
    MB. Nothing is alive when the spike is off, which confirms "create per alert, dispose after".
  - Explorer restart: the host and the composition tree are rebuilt and the video comes back.
    The old controller is disposed together with its HWND (`0x80131509`). A fresh controller on
    the new host takes 300 ms and draws again, with still 6 processes, so nothing leaked.
  - Not checked: the desktop icons (none are shown on this desktop).
  Production implications: keep all DComp work on one thread or use own-context RCWs; create a
  new controller per alert (this also covers the Explorer-restart case); close it on every exit
  path.
- [x] **T1 — Trimmed transparent alert page (warning / failed only, offline font).** Route: delegated
  writer. Strict TDD observed: RED first (13 file/text facts against
  `CosmicWin.App/Alerts/Web/*` failing with `DirectoryNotFoundException` because the files did not
  exist), then GREEN (13/13) after adding the page and the csproj `Content`/`CopyToOutputDirectory`
  item. Shipped as content, not an embedded resource, per the task's own guidance -- WebView2's
  `SetVirtualHostNameToFolderMapping` (T3) needs a real folder to point a virtual host at, not bytes
  baked into the assembly.
  - `CosmicWin.App/Alerts/Web/alert-layer.{html,css,js}`: ports `drawFailureLayer`,
    `drawFailureOverlay`, `drawFailureTitle`, `drawFailureBandIntersections`, `drawFailureModules`,
    `advanceFailureState`, and the folding-band geometry helpers they call
    (`foldingBandParameters`/`foldingBandGeometry`/`fillFoldingBandGeometry`/
    `foldingBandCompression`, `animationProgress`/`pingpong01`) from
    `backgroud-processing/script.js`. Drops the nebula/scene, stars, orbits, keyboard shortcuts,
    fullscreen, zoom, the self-check block, the canvas shake (`applyFailureShake` -- T4 shakes the
    video natively) and the backdrop pixelation (the page cannot see the video behind it). The
    `failed` theme (renamed from the source page's `error` key, matching `AlertKind.Failed`) still
    carries `shakeMs = 230`, so the page waits that long before revealing, same as the original
    timing.
  - Fully transparent html/body/canvas, one devicePixelRatio-aware canvas. API:
    `alert-layer.html#kind=failed&duration=5000` (falls back to `location.search` so the same file
    opens directly in a browser tab); posts `window.chrome.webview.postMessage('done')` when
    `duration` elapses, guarded for a plain browser with no `chrome.webview`.
  - Font: Archivo Black (SIL OFL) **downloaded successfully** from
    `github.com/google/fonts` (`ofl/archivoblack/ArchivoBlack-Regular.ttf` + `OFL.txt`) and bundled
    via a local `@font-face`; no Arial Black fallback was needed. No other network reference in the
    shipped HTML/CSS/JS (`OFL.txt` itself is excluded from that check -- it legitimately quotes an
    `http://` URL to the license text).
  - Manual check (not run by this agent -- no browser drive-by in this sandbox): open
    `alert-layer.html#kind=warning&duration=5000` in Edge and confirm the layer draws over a
    transparent/white page. A Node smoke test (stubbed DOM, no xUnit) drove the render loop for both
    `failed` and `warning` kinds through `hidden → shaking/revealing → shown → done` without
    throwing, and `node --check` confirmed the script parses.
  - Tests: `CosmicWin.App.Tests/Alerts/AlertLayerWebPageTests.cs` (13 facts) -- files land in the
    build output, no network references, hash API + done handshake present, CSS transparency, theme
    data carried over verbatim, out-of-scope pieces absent.
  - Commit `1d18e82`.
- [x] **T2 — Composition swapchain in production host (behind the gate result).** Route: delegated
  writer. Strict TDD observed: RED first (5 `CS1061` compile errors in
  `Win32VideoWallpaperHostCompositionSeamTests.cs` against members that did not exist yet), then
  GREEN (6/6) after implementing the seam; the full `CosmicWin.Interop.Tests` suite stayed green
  throughout (230 passed / 39 skipped / 0 failed, same skip count as before plus the 4 new
  desktop-gated facts) proving the switch to composition did not regress the existing D3D/attach
  behaviour.
  - `Win32VideoWallpaperHost` always presents through `CreateSwapChainForComposition`
    (`FLIP_SEQUENTIAL`, default `STRETCH` scaling) now, no env var -- re-implemented cleanly from the
    T0 spike (`spike/webview-alert-t0`, commits `b77366a`/`26fa64b`), not merged/cherry-picked. A
    root visual holds the swapchain (video) visual, rebuilt in `RebuildCompositionTarget` whenever
    the host window is (re)created (Explorer restart), on one `IDCompositionDevice` kept for the
    object's life like `_device`/`_context`.
  - Clean public seam for T3 (no `InternalsVisibleTo` hack): `Hwnd` (promoted from
    internal/test-only), `IsCompositionReady`, `CompositionGeneration`,
    `AddCompositionOverlayVisual()` (adds ONE overlay visual above the video, returned as `object`
    for `CoreWebView2CompositionController.RootVisualTarget`), `RemoveCompositionOverlayVisual()`,
    `CommitComposition()`.
  - Fixes the cross-thread DComp RCW failure T0 proved on hardware (`E_NOINTERFACE` QI'ing an RCW
    minted on the video thread from the WPF UI thread): every composition object reachable from
    another thread (device, root visual, swapchain visual, overlay visual) is reduced to a raw
    `IUnknown` pointer the instant it is created and never cached as an RCW; `CallerContextDComp<T>`
    mints a fresh RCW per call and disposes it (via `ReleaseComObject`) before returning, instead of
    leaking every wrapper forever the way the spike did on purpose. A DComp failure is caught and
    contained everywhere in the new surface; it can never stop video playback. The existing
    Direct2D overlay path (`IFrameOverlay` on the back buffer) is unaffected -- unchanged code,
    confirmed by the still-green `MediaFoundationVideoWallpaperPlayerFrameOverlayTests`.
  - Tests: `Win32VideoWallpaperHostCompositionSeamTests.cs` (6 facts, pure/unit, no desktop --
    composition-ready/generation/`Hwnd` all zero/false before attach, `Add`/`Remove`/`Commit` never
    throw with no device). `Win32VideoWallpaperHostRealAttachTests.cs` gained 4 desktop-gated facts:
    tree built after attach, overlay add/remove alongside `Present`, rebuild + generation bump after
    the host window is destroyed, and `AddCompositionOverlayVisual` called from a genuine STA thread
    (the exact T0 failure shape). All 4 compiled and were confirmed to **skip** for the documented
    reason ("CosmicWin.App is running") with `COSMICWIN_RUN_DESKTOP_TESTS=1` set -- this agent did
    not stop the running app, per its instructions. **Needs the parent's run**: close
    `CosmicWin.App`, then `COSMICWIN_RUN_DESKTOP_TESTS=1 dotnet test
    CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj --filter
    FullyQualifiedName~Win32VideoWallpaperHostRealAttachTests` to get real RED/GREEN evidence on
    hardware for these 4 facts (they were only proven to compile and to skip correctly here).
  - Commit `c9e8114`.
- [x] **T3 — Lazy transparent WebView2 alert controller (timing correction validated).** Work-unit commit `64ea1e5` (360
  additions). Delegated writer used the T2 host seam and T0 spike without T5 queue wiring.
  Creates only on alert start, closes on `done`/end/deadline/failure, watches HWND/generation,
  backs off on missing visuals, and initializes transparency per controller via WebView2 1.0.4191.47
  options. Strict TDD RED/GREEN observed for lifecycle and retry cases; 4 focused facts pass.
  Independent App suite = 1007 passed / 6 skipped; Debug solution build = 0 errors / 0 warnings.
  T6 proved process exit while idle, but timed screenshots did not show a layer; the maintainer
  independently saw layers, so absence at those instants is **not** proof the layer never draws.
  Correction commit `d5f3b16` changed failed native shake/page wait to 120 ms and failed reveal
  to 350 ms, leaving warning at 700 ms and legacy Direct2D untouched. Strict TDD RED/GREEN and
  full Interop (247 passed / 39 skipped), App (1013 passed / 6 skipped), Debug solution build
  (0 errors / 0 warnings) followed. On current Debug hardware, after queuing the alert while the
  terminal was visible and only then minimizing windows, screenshots showed the transparent red
  `FAILED` and amber `WARNING` layers over moving video, and the combined command showed one
  `FAILED` layer. These prove visible compositing, not the exact end-to-end latency or shake
  motion. Creation/navigation versus first-frame latency remains a T6 measurement gap.
  Native creation has no cancellation token: a late result closes, but a hung native creation
  cannot be forcibly terminated. Engram mirror remains pending.
- [x] **T4 — Native video shake for `failed` in the player.** Work-unit commit `851c582`:
  composition-visual transform with 230 ms decaying shake math, stop/dispose reset, and focused
  deterministic tests. 609 additions / 2 deletions in one cohesive behavior-and-tests unit; it
  exceeds the ~400-line review budget, so surface the size exception when preparing its chained
  PR slice rather than splitting code from its tests. Interop 247 passed / 39 skipped; App 1003
  passed / 6 skipped; parent spot check 23 passed. No real-hardware shake rendering check yet
  (T6), and concurrent restart-vs-expiry is not exercised by a dedicated race test.
- [x] **T5 — Queue → one WebView layer, failed precedence, setting, Direct2D switch-off.**
  Work-unit commit `b354d26` (247 additions / 12 deletions): production uses persisted
  `alerts-enabled`, creates no Direct2D alert overlay, selects failed if present, starts once per
  queued alert, shakes video once, ends on queue completion, and disposes the WebView layer before
  the host. Existing Direct2D implementation/test seams remain for rollback. Readiness holds
  pending alerts during host loss while existing keep-alive retries continue. Strict TDD RED/GREEN
  recorded for new seams/startup/reattach regressions. App suite = 1013 passed / 6 skipped;
  parent focused spot check = 10 passed; Debug solution build = 0 warnings / 0 errors.
  Hardware behavior remains T6. Engram mirror pending.
- [x] **T6 — Supervised hardware run** (same checks as live-alert-wallpaper T9, plus GPU/memory
  idle vs shown). Preflight: `CosmicWin.App` is running (one process); 18 `msedgewebview2` processes
  exist, but attribution to this app is not established. With `COSMICWIN_RUN_DESKTOP_TESTS=1`,
  the 9 `Win32VideoWallpaperHostRealAttachTests` all skipped under the desktop gate; this is not
  hardware proof. Do not terminate the running app or restart Explorer without the maintainer's
  explicit decision. After the user exits the app from its tray, run the gated tests, then a
  supervised new-app run for transparent warning/failed layers, one-kind precedence, video shake,
  FIFO, covered-desktop hold, Explorer restart, idle/shown GPU and memory, and WebView2 process
  cleanup. Separate app-owned WebView2 processes from unrelated existing ones. Maintainer chose
  `desktop-tests-only` for this run: the guarded command
  `COSMICWIN_RUN_DESKTOP_TESTS=1 dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj
  --filter FullyQualifiedName~Win32VideoWallpaperHostRealAttachTests` passed **9 / 9**, with no
  skips or failures after the app stopped. `CloseMainWindow()` returned false (tray-only process),
  non-forced `taskkill` left it running, then the parent used `Stop-Process -Force` on the observed
  PID 33944 under the maintainer's open/close authorization. This did not exercise graceful exit.
  After tests the parent relaunched the same `run/CosmicWin.App.exe`, observed PID 24084, and did
  not minimize user windows or restart Explorer. Visual, resource, WebView2 cleanup, and Explorer-
  restart checks were later explicitly authorized; see the T6 progress entry for the observed
  pass/fail results. T6 remains open because sampled visuals did not establish expected timing.

- [x] **T7 -- Retry the alert layer when its start fails (review follow-up
  `R3-displayed-before-start`).** The maintainer accepted it on 2026-09-23. Route: delegated
  writer (fix plus test). Work-unit commit `217d836`: `displayedAlert` is recorded only after
  `startAlertLayer` succeeds. A recoverable failure is recorded through `desktopTrace`
  (`alert-layer-start-failed`) and no longer escapes the watch tick, so `UpdateFocusBorder`
  still runs. A `shakenAlert` guard keeps the 120 ms shake to one per alert, and a torn-down
  layer is never ended twice. Strict TDD: RED `Assert.Equal(2, attempts)` got Actual 1, then
  GREEN. App 1014 passed / 6 skipped; Debug build 0 errors; `git diff --check` clean. Parent
  spot check: 6/6 wiring tests passed. RDD assess from `7462e1c`: medium, 109 lines,
  `under_budget`, so it stays pending in the slice. Known trade-off: a start that keeps failing
  retries and logs on every tick until the alert's duration ends.

- [x] **T8 -- Advisory test follow-ups `R3-vacuous-restart-assert` and
  `R3-real-clock-wiring-tests`.** The maintainer accepted them on 2026-09-23. Route: one
  delegated writer, two work-unit commits.
  - `1602d33`: the shake restart test now uses the production 120 ms. It advances 80 ms,
    restarts, then advances 50 ms, and asserts `Compute(50)`, not the trivial identity. A new
    theory covers the 119 / 120 ms expiry boundary. Sabotage RED: when the restart kept the
    original start, the test failed; reverted, GREEN 9/9.
  - `b472835`: `AppComposition.Wire` takes an optional `TimeProvider`; the default is
    `TimeProvider.System` and production does not set it. It is used for the three alert-queue
    time reads. The wiring tests advance a manual fake clock instead of `Thread.Sleep(1100)`:
    6 tests went from ~3 s to ~80 ms.
  Checks: Debug build 0 errors; Interop 249 passed / 40 skipped; App 1014 passed / 6 skipped;
  `git diff --check` clean. Parent spot check: 6/6 wiring tests and 9/9 shake tests passed. RDD
  assess from `7462e1c`: medium, 227 lines, `under_budget`, so it stays pending in the slice.

- [x] **T9 -- Permanent preloaded alert layer, telemetry, lower latency (fixes T6 F1/F2).** The
  maintainer accepted it on 2026-09-24. Route: one delegated writer (4+ non-trivial files:
  controller, page, wiring, tests), one work-unit commit per sub-task, strict TDD.
  - [x] **T9a -- Alert-layer telemetry.** Commit `98c72e8` (292 additions / 27 deletions).
    `WebViewAlertLayerController` and `AlertLayerLifecycle` both take an optional
    `Action<string>? trace`, wired to `desktopTrace.Record` in `WireProduction` (a `desktopTrace`
    local is now built before the controller so both share the same sink). `AlertLayerTrace` owns
    the exact wording for create start (hwnd, generation), environment/controller ready (elapsed
    ms), navigation completed (success/`WebErrorStatus`, elapsed ms), show (kind, duration), hide,
    `done`, close (reason), process failure (kind/reason), and an error line for every one of the
    13 previously-silent `Debug.WriteLine` catch sites plus the silent no-overlay-visual path (kept
    alongside `Debug.WriteLine`, not instead of it). Strict TDD: RED first for `AlertLayerTrace`
    (`CS0103`, the type did not exist), GREEN 11/11 after adding it; then a second RED/GREEN cycle
    for the controller wiring itself -- the two new `WebViewAlertLayerControllerTests` facts were
    written and confirmed to fail (`CS1739`, no `trace` parameter) against the UNCHANGED controller
    (verified by stashing the implementation edit), then the implementation was reapplied and both
    passed. App suite = 1027 passed / 6 skipped (1014 baseline + 13 new); Debug solution build = 0
    errors, the same 2 pre-existing unrelated warnings; `git diff --check` clean.
    Testability limit (same class as T3/T6): `Start`/`End`'s "show"/"hide" trace lines are exercised
    behaviorally (an unattached host never reaches WebView2, proven by asserting no "close" line
    fires either); create start/ready, navigation, and process-failed only fire once a real WebView2
    environment/controller exists, so those call sites are proven present by a structural test
    (`EveryLifecycleEventAndSilentFailurePathIsTraced`) rather than exercised -- real confirmation is
    T9e hardware.
  - [x] **T9b -- Page show/hide message API.** Commit `eb6c548` (144 additions / 34 deletions).
    `alert-layer.js` listens for `window.chrome.webview` `message` events carrying
    `{type:"show", kind, duration}` / `{type:"hide"}`. Idle: canvas cleared, no
    `requestAnimationFrame` loop running (`animating` flag guards `render`). `show` resets
    `failureStartMs`/`failureState`/`doneSignaled` and starts the loop exactly like a fresh load
    (same shakeMs wait + reveal); calling `show` again while already showing restarts from zero
    with the new kind WITHOUT double-queuing a frame (`if (!animating) scheduleFrame(render)`).
    `hide` clears and stops; `done` is still posted when the page's own duration elapses; `ready`
    is now posted once the script has initialised, so the host (T9c) knows navigation produced a
    live page. The `#kind=/&duration=` hash (and `?kind=/&duration=` query) API still auto-shows
    for a plain browser tab, but a BARE navigation (host preload, T9c) no longer auto-shows a
    default warning -- gated on `hasExplicitParams`.
    Strict TDD: RED first for 3 new `AlertLayerWebPageTests` facts (`hasExplicitParams`,
    `addEventListener("message"`, `"ready"` all absent from the unchanged script -- 3 failures),
    then GREEN 16/16 after the rewrite. `node --check` passed. Node was available
    (`node --version` -> v24.19.0), so a stubbed-DOM smoke test (fake `document`/`canvas`/
    `requestAnimationFrame`/`chrome.webview`, driven with `vm.createContext`) additionally proved
    10 behavioral scenarios pass: bare-load idles and posts `ready`; hash auto-show still starts,
    loops, and posts `done` at its own duration; message-driven `show` starts exactly one frame;
    a restart mid-show does not double-queue; `hide` leaves no frame scheduled once any in-flight
    one drains; a fresh `show` after `hide` restarts. This script and its stubbed sandbox were NOT
    committed (throwaway, same as T1's manual check). App suite = 1030 passed / 6 skipped; Debug
    solution build = 0 errors, same pre-existing warnings; `git diff --check` clean.
    Not verified: real WebView2/Edge rendering of the show/hide transitions -- T9e hardware.
  - [x] **T9c -- Persistent controller.** Commit `1776cc2` (551 additions / 237 deletions -- exceeds
    the ~400-line review budget; it is one cohesive rewrite (production + its pure state machine +
    tests + wiring), so it stays a single unit rather than splitting code from its tests, same size
    exception T4 disclosed). Replaces the old per-alert create/dispose `WebViewAlertLayerController`
    with a permanent preload:
    - New pure `AlertLayerPreloadState` (host identity change detection, exponential
      creation/process-failure backoff via `Failed()`/`CanCreate`/`Created()`, `Ready`/`Visible`
      flags, and one pending show with an absolute deadline via `RequestShow`/`ApplyPendingShowIfDue`).
      Fully unit-tested (10 facts), no WebView2 involved. Supersedes and removes the old
      `AlertLayerLifecycle` (deadline-based auto-close did not fit a controller that must survive
      past any one alert's duration) and its 3 now-obsolete tests.
    - `Preload()` creates the environment/composition controller once, adds the overlay visual, and
      navigates the BARE page (no `#kind=`/`&duration=` hash any more -- T9b's idle page). Ready =
      navigation completed AND the page's own `"ready"` message (both tracked, `TryMarkReady`).
      `Start`/`End` now post JSON `{"type":"show",...}`/`{"type":"hide"}` via
      `PostWebMessageAsJson` and flip `IsVisible`, instead of creating/disposing anything --
      `End` NEVER tears down the controller (proven both behaviorally, an unattached host reaches
      neither WebView2 nor a "close" trace line across two Start/End cycles, and structurally, `End`'s
      own method body contains no `TearDown(` call).
    - The 250ms poll now runs for the whole preloaded lifetime (started by `Preload()`, not by
      `Start()`), detecting a host HWND/generation change or `IsCompositionReady` loss and
      recreating with the same exponential backoff; `ProcessFailed` also triggers recreate. The
      environment is kept across an ordinary host-change recreate and dropped ONLY for
      `"create-failed"`/`"process-failed"` (feature doc's "Keep _environment ... unless creation
      failed/process failed"), proven structurally (`TearDown("host-changed")` has no
      `dropEnvironment: true`, the other two do).
    - `AppComposition` gains a `preloadAlertLayer` seam, invoked once on the owning UI thread inside
      the existing `alertsEnabled` block (same place the alert pipe server starts), wired from
      `WireProduction` as `alertLayer.Preload`. `alertRendererReady` is UNCHANGED
      (`videoWallpaperHost.IsCompositionReady`) -- not gated on WebView readiness, since a pending
      show already covers that race.
    Strict TDD: RED first for `AlertLayerPreloadStateTests` (`CS0246`, type did not exist) -> GREEN
    10/10, covering exactly the 6 named cases (pending show applied with remaining duration; pending
    show dropped once expired; host identity change detection; failure backoff/recreate timing;
    `Hide` clears visibility without touching `Ready`; `RequestShow` still posts while already
    visible). Then RED for the 4 new/changed `WebViewAlertLayerControllerTests` facts against the
    OLD controller (`CS1061`, no `Preload` method) -> GREEN 7/7 after the rewrite. Then RED for 2 new
    `WebViewAlertCompositionWiringTests` facts (`CS1739`, no `preloadAlertLayer` parameter on `Wire`)
    -> GREEN after wiring it through. App suite = 1042 passed / 6 skipped (net +12 over T9b: +10
    state tests, +2 wiring tests; the 7 controller facts are a like-for-like replacement of the old
    7); Interop unaffected (249 passed / 40 skipped); Debug solution build = 0 errors, same
    pre-existing warnings; `git diff --check` clean.
    Not verified (hardware-only, same limitation as T3/T6): real preload creation/navigation timing,
    real Explorer-restart recreate, real `ProcessFailed` recovery, and the Idle-cost GPU condition
    itself -- all T9e.
  - [x] **T9d -- Immediate queue tick on enqueue.** Commit `a8cf075` (39 additions / 7 deletions).
    `HandleAlertCommand` (runs on the pipe server thread) now calls `onOwningThread(UpdateAlertOverlay)`
    right after a successful `Enqueue`, reusing the existing dispatcher seam so the update stays on
    the UI thread and is serialized with the ordinary 400 ms watch tick rather than racing it.
    Strict TDD: RED first (`Assert.Single` failed, "The collection was empty" -- nothing had run yet
    without a tick), then GREEN after the one-line addition. Fixing this surfaced a pre-existing gap
    in 3 OTHER wiring tests that enqueue-then-fire-one-tick and assert exactly one recorded overlay
    call: the legacy Direct2D path (`setAlertOverlayTiles`, untouched per the feature doc) has no
    dedup on an unchanged active alert, so it now legitimately (and harmlessly, same idempotent tile
    data) fires twice -- once from the immediate tick, once from the watch tick. Updated those 3
    tests (`AlertDesktopVisibilityWiringTests.AnUncoveredDesktop_ShowsAQueuedAlert`,
    `AlertWallpaperWiringTests.ValidAlertCommand_IsAcceptedAndUpdatesOverlayOnTickInCommandOrder`,
    `.ValidAlertCommand_TilesAreOffsetByTheWorkAreasOriginOnTheMonitor`) to read the LATEST recorded
    tile set instead of asserting exactly one call. App suite = 1043 passed / 6 skipped (net +1);
    Interop unaffected (249 passed / 40 skipped); Debug solution build = 0 errors, same pre-existing
    warnings; `git diff --check` clean.
  - [x] **T9e -- Hardware re-run.** First alert after launch, latency (warm and first), FIFO with
    3 s alerts, covered hold, Explorer restart, GPU/memory while hidden vs shown.
    2026-09-24 run on the T9 Debug build (copied to a scratch folder so builds are not locked),
    same screen-sampling probe as T6. Passed:
    - Preload telemetry at startup: environment 11 ms, controller 327 ms, navigation 2037 ms. So
      navigation was most of the old per-alert latency.
    - **F1 fixed:** the first alert after launch appears at 0.69 s. Warm `failed` 5 s: 0.45 s and
      0.42 s to visible, visible until ~5.15 s. Warning 5 s: 0.55 s to 5.19 s. Trace shows
      `show` -> `done` 5.0 s apart.
    - FIFO, sequential client calls `warning:1 duration:3` then `failed:1 duration:3`: trace shows
      warning show/done/hide, then failed shown 0.4 ms after that hide, 3 s each. (The earlier
      concurrent-launch probe was racy; discarded.)
    - Covered hold: a probe cover opened *before* the command, forced to foreground (verified
      `fg == cover`), for 5.02 s. The layer appeared 0.24 s after the cover left, full 5 s.
      Probe gotchas: a WinForms `Show()` from a background PowerShell does not take the
      foreground (foreground lock), and T9d's immediate tick promotes an alert before a cover
      opened *after* the command. Both runs were discarded as probe artifacts.
    - GPU while hidden: per-process GPU engine counters (5 x 1 s) show every `msedgewebview2`
      below 0.1 %. `CosmicWin.App` itself is 24 % summed over all engines, including video decode,
      which predates T9. 3D total: idle 2.64 %, shown 45.24 %, after 2.46 %.
    - Memory: preloaded idle 7 WebView procs, 390 MB private (accepted cost); shown 455 MB; app
      private ~241-266 MB.
    Explorer restart and `ProcessFailed` were run after `8e1c0fa`, with the maintainer's
    explicit authorization for one Explorer restart, on the redeployed fixed build:
    - Explorer restart (PID 11560 -> 2584, restarted on its own): the alert
      (`failed`, 20 s) arrived just after the kill. `post-show` threw (controller already closed),
      then `close reason=host-changed`, recreate on generation 2 (controller 356 ms, navigation
      2025 ms). **The alert was not lost** (the `8e1c0fa` requeue path). It showed, and `done`
      came 20.0 s after its `show`, so the original deadline was kept. Gap: the probe saw red only
      ~4 s after navigation completed. There is no trace line for when a pending show is applied,
      so that latency is unexplained.
    - `ProcessFailed` (browser process killed): `process failed kind=BrowserProcessExited
      reason=Unexpected`, close, recreate 0.7 s later, navigation 2029 ms. The next warning
      showed at 0.56 s for its full 5 s.
    - Shake visual (2026-09-24, closes T6): raw 1200x500 top-left frames every ~11 ms for
      450 ms after the command, `failed` vs a `warning` control (no shake). In the control, the
      fixed background elements (blue glow at top, orange streak, stars) stay pixel-still across
      all frames. In `failed`, the whole video shifts and zooms back and forth from ~90 ms to
      ~230 ms: blobs jump frame to frame, and a zoomed white shape appears and disappears in
      alternate frames. It is still by 259 ms, and the red reveal follows (visible by ~400 ms).
      So the native shake renders, then the page reveals.
      A pixel-diff-only probe was ambiguous (the WebView becoming visible also spikes), so the
      frames were judged visually.

- [x] **T10 -- Explain the ~4 s gap after an Explorer-restart recovery.** Accepted by the
  maintainer on 2026-09-24. T9e saw red ~4 s after the recreated page's navigation completed,
  and there is no trace line for when a pending show is applied. Route: inline (one mechanical
  trace addition plus its tests), then a hardware re-measure with one Explorer restart.
  - [x] **T10a -- Trace page ready and pending-show application.** Add `alert-layer page ready`
    and `alert-layer pending show applied kind=<k> remaining=<ms>`. Strict TDD. Commit
    `61edb3f`: RED `CS0117` x2 (members missing), GREEN 13/13 trace tests, App 1047 passed / 6
    skipped, Debug build 0 errors, `git diff --check` clean.
  - [x] **T10b -- Re-measure on hardware**, and decide whether a fix is needed. 2026-09-24,
    one Explorer restart (PID 2584 -> 19304, restarted on its own) *while* a 20 s `failed` layer
    was visible. Times are relative to the client launch: show 0.29 s -> red 0.70 s; Explorer
    killed 3.21 s -> layer gone 3.25 s; `close reason=host-changed` 3.55 s; recreate from 4.33 s
    (controller 407 ms, navigation 2027 ms); `page ready` and `pending show applied
    remaining=13518` at 6.77 s; **red again at 7.14 s (0.37 s, the normal reveal)**; `done` at
    20.30 s, the original deadline. **The ~4 s gap did not reproduce; no fix needed.**
    Recovery costs ~3.9 s from the kill to visible again, and the page's ~2 s navigation is most
    of it. The T9e ordering (restart *before* the command) was not re-run; its extra delay stays
    unexplained, and a probe artifact from windows restored by the restart is the likely
    suspect.

- [x] **T11 -- Drop the `.local` virtual host (the ~2 s page navigation).** 2026-09-25, the
  maintainer asked to keep advancing. The WebView2 reference for
  `SetVirtualHostNameToFolderMapping` says: "using `.local` as the top-level domain name will work
  but can cause a delay during navigations. You should avoid using `.local` if you can." Route:
  inline (one mechanical rename plus its structural test). Strict TDD: RED 2/2 (the updated navigate
  assertion and the new `VirtualHostUsesAReservedExampleDomainNotDotLocal`), then GREEN, App 1048
  passed / 6 skipped. Hardware: navigation **2022-2037 ms -> 160, 177, 195 ms** over three
  startups; a `failed` alert still renders (red 0.73-4.34 s for a 4 s alert).

## Progress

2026-09-23: document created; page located; T0 next.

2026-09-23: T0 passed on hardware (see T0 entry).

2026-09-23: T1 and T2 implemented by a delegated writer, strict TDD observed for both (RED then
GREEN, see each entry above). Verification: `dotnet build CosmicWin.sln -c Debug` succeeded (0
errors, only 3 pre-existing warnings unrelated to this work);
`dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj` = 230 passed / 39 skipped / 0
failed; `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj` = 1003 passed / 6 skipped / 0
failed. The 4 new desktop-gated composition facts in `Win32VideoWallpaperHostRealAttachTests.cs`
compiled and skipped for the documented reason (CosmicWin.App running) -- not yet exercised for
real on hardware. Commits: `1d18e82` (T1), `c9e8114` (T2).

2026-09-23 recovery: an earlier session left an uncommitted T4 native shake across 11 paths. A delegated
writer preserved it and fixed shake restart/expiry serialization and Stop/Dispose cleanup. The
inherited focused tests passed before the first fix (no observed RED); the new Stop/Dispose
regressions failed twice before the cleanup and passed twice afterward. Independent verification:
`dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj` = 245 passed / 39 skipped before
the teardown fix; the writer's post-fix run = 247 passed / 39 skipped. App tests = 1003 passed / 6
skipped before that fix. `git diff --check` passed. No hardware transform rendering check or
concurrent restart test yet. T4 remains unchecked and uncommitted; its Engram mirror is pending
because the local Engram endpoint reported an ownership mismatch. The native assessment of the
untracked candidate was unassessable; a separate verifier checked the earlier revision. After the cleanup, an independent
verifier ran Interop (247 passed / 39 skipped) and App (1003 passed / 6 skipped); the parent reran
23 focused tests (all passed) and `git diff --check` (passed). The feature-branch-chain strategy
was selected. T4 was committed as `851c582`; its 5-second join-timeout teardown caveat and
hardware transform rendering remain unverified.

2026-09-23 T3 in progress: delegated writer added a lazy composition controller, WebView2 package,
and four lifecycle/structural tests. Strict TDD RED observed for the new lifecycle seam,
null-overlay retry, and a process-global configuration guard, then GREEN. App suite = 1007 passed /
6 skipped; solution Debug build succeeded with 0 warnings. The supported WebView2 1.0.4191.47
`CoreWebView2ControllerOptions.DefaultBackgroundColor` and composition-controller options overload
replace the process-wide transparency environment variable (Microsoft Learn WebView2 API reference;
installed NuGet XML). The deadline closes a successfully created controller if its page never posts
`done`, with exponential retry backoff for failed creation or a missing overlay. WebView2 environment/
controller creation has no cancellation token: a late completion is closed, but a hung native
creation task cannot be forcibly stopped. Real WebView2 process teardown, transparent composition,
and Explorer-restart behavior remain hardware checks; lifecycle tests exercise the production-used
state path but not a real controller. T3 was committed as `64ea1e5` after independent read-only verification; T6 retains the
hardware checks.
Engram mirror remains pending due to provider ownership mismatch.

2026-09-23 T5 in progress: delegated writer wired the existing queue to one WebView layer
(`failed` wins), one 230 ms shake per failed alert, transition-based start/end, persisted
`alerts-enabled` gate, and production Direct2D switch-off. The renderer-ready predicate holds
pending alerts through host loss. A regression test caught that clearing `videoWallpaperActive`
on a transient keep-alive attach failure would prevent future retries; that flag change was removed.
A startup STA test also caught premature synchronization-context validation; the controller now
constructs on STA before the WPF dispatcher starts pumping, but still checks the context at Start.
Strict TDD RED/GREEN was observed for new seams and these regressions. Independent App suite =
1013 passed / 6 skipped and Debug solution build succeeded with 0 warnings. Parent spot check =
10 focused passed; `git diff --check` clean. Real desktop composition, process lifetime, GPU shake,
and Explorer restart remain T6 hardware checks. T5 committed as `b354d26`; Engram mirror pending.

2026-09-23 T6 full hardware continuation (maintainer authorized Explorer restart and temporary
window minimization, with restoration of the terminal): current Debug build launched directly from
`CosmicWin.App/bin/Debug/net10.0-windows10.0.19041.0` as PID 1288; the pre-existing
`run/CosmicWin.App.exe` was older and was not overwritten. Combined `warning:2 failed:1 duration:5`
and isolated `warning:1 duration:10` returned client exit 0. Two screenshots during each alert
(1.2 and 2.1 seconds after the command) showed animated video but **no visible failed/warning
layer**. The maintainer saw layers at another time: these captures establish a timing-dependent
observation, not a definitive "never renders" failure or a root cause. Six
`msedgewebview2` processes descended from PID 1288 during an alert, zero before and after;
non-app WebView2 processes were excluded. Memory sample: app private 278 MB idle, 279 MB shown,
279 MB after; app-owned WebView2 private 0 → 161 → 0 MB and working set 0 → 282 → 0 MB.
The GPU counter sampler timed out without a usable result, so GPU cost is **unavailable** (no
estimate inferred). A single authorized Explorer restart changed its PID 38072 → 27944 while
the Debug app remained PID 1288. Afterward the video still animated (495/504 sampled pixels
changed across 900 ms) and the client accepted another warning, but its visible layer was still
absent. One-kind precedence, FIFO visuals, covered-desktop reveal, and native shake appearance
cannot be verified while the layer is invisible. The restored user session has Explorer PID 27944
and the original `run/CosmicWin.App.exe` PID 34824; user windows and terminal were restored.
Temporary screenshots/scripts and the isolated Edge profile were removed. No source edits were
made. Root cause remains unknown; production has no navigation/render telemetry. The Engram mirror
is still pending because its provider rejected ownership.

2026-09-23 native review of the whole feature range `b7f4ce6..HEAD` (24 files, 2821 lines, risk
medium, `slice_budget_reached`; no earlier boundary had been reviewed). The maintainer granted
consent. Lineage `review-ecb8d681c25b93b6`, one reliability lens. The stale managed assets
needed one `gentle-ai sync` first. The review found one CRITICAL candidate-caused finding
`R3-dcomp-raw-pointer-race`: the five raw DirectComposition pointer fields in
`Win32VideoWallpaperHost` were read and released from the UI, player and video threads with no
lock, a native use-after-free/double-release. The parent confirmed there was no lock. Correction
commit `7462e1c` (`fix(interop): serialize DirectComposition pointer access across threads`),
route: delegated writer. It adds one innermost `_compositionLock`, keeps the existing bodies as
`...Unlocked` methods and adds thin locked entry points; lock order is `_shakeGate` then
`_compositionLock`, never inverted. The first attempt wrapped the bodies (457 raw changed lines)
and was rejected by the 200-line frozen correction budget. It was amended to 164 lines. RED was
**not observable**: the new desktop-gated stress fact
`CompositionSeam_HammeredFromTwoThreadsWhileTheHostWindowIsRebuilt_NeverCrashesOrThrows` skips
while `CosmicWin.App` runs. Checks: Debug build 0 errors; Interop 247 passed / 40 skipped; App
1013 passed / 6 skipped; `git diff --check` clean. Targeted validation **approved**, and the
acknowledgement burned authority. The reviewed boundary is now `7462e1c`.
Advisory, non-blocking follow-ups (not accepted as scope yet):
- `R3-displayed-before-start` (WARNING), `CosmicWin.App/AppComposition.cs:453-461`:
  `displayedAlert` is set before `startAlertLayer`. If `Start` throws, the alert is never retried
  and the exception escapes into the timer callback.
- `R3-real-clock-wiring-tests` (SUGGESTION),
  `CosmicWin.App.Tests/Alerts/WebViewAlertCompositionWiringTests.cs:96-97`: the tests use a real
  clock.
- `R3-vacuous-restart-assert` (SUGGESTION),
  `MediaFoundationVideoWallpaperPlayerShakeTests.cs:139-141`: the restart assertion is trivially
  zero at 200 ms > 120 ms, and the tests use 230 ms while production uses 120 ms.

2026-09-24 T6 agent-driven hardware run (maintainer authorized closing the app and minimizing
windows; Explorer untouched). Elevated shell. The Release app (PID 27056) was force-stopped; the
Debug build was launched for the run; afterwards the Release app was relaunched (PID 28324, video
`tryPlay=True`) and user windows restored. Temporary probe scripts' outputs were removed.
- Gated desktop tests, app closed: `Win32VideoWallpaperHostRealAttachTests` **10 / 10 passed**, no
  skips, including the `7462e1c` stress fact
  `CompositionSeam_HammeredFromTwoThreadsWhileTheHostWindowIsRebuilt_NeverCrashesOrThrows` (first
  real run). Full Interop with the gate on: 274 passed / 15 skipped / 0 failed.
- Method: full-screen captures every ~30 ms, sampled every 24 px, counting strongly red / amber
  pixels (per mille); time zero is when the client is launched; windows minimized first.
- **FINDING F1 -- the first alert after app launch never appears.** Reproduced on 3 of 3 launches
  (first alert sent ~23 s, ~10 s and ~45 s after launch; probe windows 7-12 s): no red pixels at
  all. The very next alert on the same process appears. The app's `msedgewebview2` children
  appeared only ~15 s after the command and lived ~5 s. A DBWIN listener (self-tested working)
  saw **no** `Debug.WriteLine` output from the app, so no caught exception was logged. Root cause
  unknown; production still has no alert-layer telemetry.
- **FINDING F2 -- WebView startup eats the alert's duration.** Warm process, 5 s `failed`: first
  visible at 2.84 / 2.87 / 2.90 / 2.99 / 2.78 s, gone at ~5.5-5.6 s, so ~2.7 s of 5 s shown. The
  queue deadline starts at promotion, the layer ~2.4 s later. FIFO `warning:1 duration:3` then
  `failed:1 duration:3`: the warning showed for 0.6 s (2.97-3.60 s) and the failed never appeared.
- Warning (warm): amber at 3.10 s, gone at 5.71 s. Combined `warning:2 failed:1`: only red
  (3.01-5.75 s), so failed wins and one layer shows.
- Covered desktop (borderless, non-maximized, screen-sized TOPMOST probe for 0-5.09 s): no layer
  while covered; red from 7.83 s to 10.55 s, so the alert was held and its duration started after
  the cover left. (A first attempt used a *maximized* cover, which the detector excludes by
  design; that run is discarded.)
- GPU 3D total (3 x 1 s samples) / memory: idle 0.86 %, app private 217 MB, 0 WebView procs;
  shown (`failed`) **42.12 %**, app 252 MB, 6 WebView procs, 461 MB WebView private; after 0.94 %,
  0 WebView procs.
- Not verified: the 120 ms native shake visual (too short for this sampler).

## Next step

The failed-timing correction is committed as `d5f3b16` (120 ms shake, 350 ms failed reveal;
warning still 700 ms). Isolated failed, warning, and combined-failed layers were photographed on
current Debug hardware after submitting the command with the terminal visible and then exposing
the desktop. The earlier screenshots that minimized every window before submission did not prove
that the layer never rendered. After testing, the original older `run/CosmicWin.App.exe` was
restored as PID 32468, windows/terminal restored, and temporary screenshots/scripts removed.
T6 remains open for end-to-end latency, native shake visual, covered-desktop/FIFO behavior, and
bounded GPU measurement; do not claim exact 470 ms from the pipe command because WebView startup
and queue polling precede the page animation. T1 manual Edge check,
T2's four real desktop-gated facts, and T4 transform rendering on hardware remain pending. Do not
run the gated desktop tests while CosmicWin.App is running.
Review: feature range approved through `7462e1c`. Still pending: run the new gated stress fact with
the app closed, and all three advisory follow-ups are done (T7, T8). Unreviewed slice since `7462e1c`: 227 lines,
under budget.

2026-09-24 T9a-d implemented by a delegated writer, one commit per sub-task, strict TDD RED/GREEN
observed for every one (see each sub-task entry above for the exact evidence):
`98c72e8` (T9a telemetry), `eb6c548` (T9b page show/hide), `1776cc2` (T9c persistent controller,
788 lines -- disclosed size exception, same class as T4's), `a8cf075` (T9d immediate tick). Final
verification after all four: `dotnet build CosmicWin.sln -c Debug` = 0 errors, 3 pre-existing
warnings unrelated to this work; `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj` =
1043 passed / 6 skipped / 0 failed (baseline 1014/6); `dotnet test
CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj` = 249 passed / 40 skipped / 0 failed
(unchanged from baseline); `git diff --check` clean on every commit. T9 stays open: T9e (supervised
hardware re-run -- first-alert latency, warm latency, FIFO with 3s alerts, covered hold, Explorer
restart, idle vs shown GPU/memory) is the parent's to run, not this writer's. Design decisions the
parent should know: `Preload()` is called once, on the owning UI thread, inside `Wire`'s existing
`if (alertsEnabled)` block (same place the alert pipe server starts) -- not gated on the video
wallpaper's own startup activation, since the alert layer's readiness is independent of it. A
pending show's remaining duration is computed as `deadline - now` at the moment the page becomes
ready (navigation completed AND its own `"ready"` message received), never re-derived from the
original duration. The old per-alert `AlertLayerLifecycle` class and its 3 tests were removed
(superseded, not extended) because its deadline-based auto-close does not fit a controller that
must now survive past any one alert's duration.

2026-09-24 native review of `7462e1c..0fa5aba` (T9a-e: 14 files, 1600 lines, risk medium,
`slice_budget_reached`). The maintainer granted consent. Lineage `review-f4fee9cc7e38696e`, one
reliability lens. It found one CRITICAL, deterministic, candidate-caused finding,
`R3-stale-ready-after-teardown`: a `host-changed` or `host-not-ready` teardown never cleared
`AlertLayerPreloadState.Ready`. A `Start` during the ~2 s recreate was marked visible with no
controller and was silently lost. The parent confirmed it in the code. Correction commit `8e1c0fa`
(route: delegated writer, 74 of the 90 declared lines) adds `ControllerLost()`, which `TearDown`
now calls for every reason. It clears `Ready`, and it requeues an on-screen alert as pending with
its original deadline, so after an Explorer restart it re-shows for the remaining time only. RED
was a `CS1061` for the missing `ControllerLost`; GREEN is 12/12 state tests, App 1045 passed / 6
skipped, Debug build 0 errors, `git diff --check` clean. Parent spot check: 12/12. Targeted
validation **approved**, and the acknowledgement burned authority. The reviewed boundary is now
`8e1c0fa`. T9e still lacks Explorer-restart and `ProcessFailed` checks on hardware; the new
requeue path is only unit-tested.
