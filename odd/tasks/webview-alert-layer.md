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
- **Idle cost:** the WebView2 is created when an alert starts and disposed when it ends (the page
  measured ~28 % GPU and 400–500 MB in Edge). No WebView2 process exists while idle.
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
- [ ] **T6 — Supervised hardware run** (same checks as live-alert-wallpaper T9, plus GPU/memory
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

2026-09-23 recovery: Claude left an uncommitted T4 native shake across 11 paths. A delegated
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
