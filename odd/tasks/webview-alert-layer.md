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
- [ ] T1 — Trimmed transparent alert page (warning / failed only, offline font), embedded as an
  app resource.
- [ ] T2 — Composition swapchain in production host (behind the gate result).
- [ ] T3 — WebView2 alert layer: lazy create on alert start, dispose on end, show one kind.
- [ ] T4 — Native video shake for `failed` in the player.
- [ ] T5 — Wiring: queue → layer (failed wins), Direct2D overlay switched off; setting.
- [ ] T6 — Supervised hardware run (same checks as live-alert-wallpaper T9, plus GPU/memory idle
  vs shown).

## Progress

2026-09-23: document created; page located; T0 next.

2026-09-23: T0 passed on hardware (see T0 entry).

## Next step

T1 (trimmed transparent alert page) and T2 (composition swapchain in the production host).
