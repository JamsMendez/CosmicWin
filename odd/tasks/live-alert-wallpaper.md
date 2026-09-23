# Live alert wallpaper

## Objective

An external command such as `CosmicWin.exe --alert "warning:2 failed:1"` shows N alert tiles
(warning or failed) over the video wallpaper for a few seconds, then the video continues alone.

## Why

Maintainer request, 2026-09-23. Full analysis, measurements and design:
`docs/research/live-alert-wallpaper-plan.md`.

## Scope

In scope: primary monitor, kinds `warning` / `failed`, named-pipe command channel, alert layer
drawn natively with Direct2D / DirectWrite on the existing swapchain back buffer.
Out of scope: multi-monitor, WebView2 or any browser engine, HTTP listeners.

## Constraints

- Native Windows / .NET only, no third-party runtime dependency (route decided 2026-09-23:
  Direct2D, WebView2 rejected).
- `CosmicWin.Interop` is the only project touching Win32.
- Idle cost must stay zero: no drawing when no alert is on screen.
- A failing overlay must never break video playback.

## Open question

An alert that arrives while a fullscreen window covers the desktop: drop it with a log line, or
queue it with a max age. Must be decided before T2.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` per project. T0 is a throwaway hardware
spike, checked manually, not TDD.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: ~1200 authored lines over T1–T8, so chaining will be
asked when the running count passes ~400. RDD: on (global).

## Tasks

- [ ] **T0 — Spike (gate):** throwaway branch `spike/alert-overlay-t0`. Direct2D rectangle and
  DirectWrite text drawn in `MediaFoundationVideoWallpaperPlayer.Tick()` between
  `TransferVideoFrame` and `Present`. Check on hardware: visible under the icons, survives the
  400 ms re-attach and an Explorer restart, GPU with and without the overlay (resolution and
  refresh recorded). Route: delegated writer (CsWin32 Direct2D interop, preparation reading),
  hardware run by the parent.
- [ ] **T1 — `AlertCommandParser`**
- [ ] **T2 — `AlertQueue`** (needs the open question answered)
- [ ] **T3 — `AlertTileLayout`**
- [ ] **T4 — Named pipe server and `--alert` client**
- [ ] **T5 — `IFrameOverlay` seam in the player**
- [ ] **T6 — `Direct2DAlertOverlay`**
- [ ] **T7 — Alert visuals ported from great-sage**
- [ ] **T8 — Wiring and `alerts-enabled` setting**
- [ ] **T9 — Supervised hardware run**

Task details and checks: plan §6.

## Progress

2026-09-23: branch `feat/live-alert-wallpaper` created, T0 started. Engram mirror `odd/live-alert-wallpaper/tasks`: **pending** (save refused, several active sessions matched).

## Next step

T0 spike.
