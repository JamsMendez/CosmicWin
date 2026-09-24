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
- [x] **T3 — Lazy transparent WebView2 alert controller.** Work-unit commit `64ea1e5` (360
  additions). Delegated writer used the T2 host seam and T0 spike without T5 queue wiring.
  Creates only on alert start, closes on `done`/end/deadline/failure, watches HWND/generation,
  backs off on missing visuals, and initializes transparency per controller via WebView2 1.0.4191.47
  options. Strict TDD RED/GREEN observed for lifecycle and retry cases; 4 focused facts pass.
  Independent App suite = 1007 passed / 6 skipped; Debug solution build = 0 errors / 0 warnings.
  Real WebView2 transparency, process exit while idle, and Explorer-restart behavior remain T6
  hardware checks. Native creation has no cancellation token: a late result closes, but a hung
  native creation cannot be forcibly terminated. Engram mirror remains pending.
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
- [ ] T6 — Supervised hardware run (same checks as live-alert-wallpaper T9, plus GPU/memory idle
  vs shown).

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

## Next step

T5 committed as `b354d26`. Next T6 supervised desktop/resource validation; do not claim the idle
process-lifetime guarantee until measured. T1 manual Edge check,
T2's four real desktop-gated facts, and T4 transform rendering on hardware remain pending. Do not
run the gated desktop tests while CosmicWin.App is running.
