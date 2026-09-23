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

- [ ] **T0 — Spike (gate), throwaway branch `spike/webview-alert-t0`.** Switch the host's
  swapchain to composition (`CreateSwapChainForComposition` + DComp target on the host HWND),
  add a transparent WebView2 composition visual above it loading a stub page (translucent red
  band + text). Check on hardware: video visible through the transparent page, below the icons,
  survives the 400 ms re-attach and an Explorer restart; GPU/memory with the WebView2 alive vs
  disposed; create-to-first-paint latency. Route: delegated writer, hardware run by the parent.
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

## Next step

T0 spike.
