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

## Decided: alerts while the desktop is covered

Maintainer, 2026-09-23: an alert that arrives while a fullscreen window covers the desktop is
**queued with a max age** (~5 min). It is shown once the desktop is visible again; past the max
age it is dropped with a log line. T2 implements it.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` per project. T0 is a throwaway hardware
spike, checked manually, not TDD.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: ~1200 authored lines over T1–T8. Chain strategy
chosen by the maintainer on 2026-09-23: **`stacked-to-main`**. RDD: on (global).

Slices (planned, each a PR against main stacked on the previous one):

1. `fix/explorer-restart-reattach` (base fix, already done).
2. T1 + T3: parser and tile layout (pure logic).
3. T2 + T4: queue and named pipe / `--alert` client.
4. T5 + T6: overlay seam and Direct2D overlay.
5. T7 + T8: visuals and wiring. T9 evidence goes with slice 5.

## Tasks

- [x] **T0 — Spike (gate):** throwaway branch `spike/alert-overlay-t0`. Direct2D rectangle and
  DirectWrite text drawn in `MediaFoundationVideoWallpaperPlayer.Tick()` between
  `TransferVideoFrame` and `Present`. Check on hardware: visible under the icons, survives the
  400 ms re-attach and an Explorer restart, GPU with and without the overlay (resolution and
  refresh recorded). Route: delegated writer (CsWin32 Direct2D interop, preparation reading),
  hardware run by the parent.
  **Partial, 2026-09-23.** Spike commit `d9fe4a2` on `spike/alert-overlay-t0` (not to be merged;
  `SpikeAlertOverlay.cs`, toggle `COSMICWIN_SPIKE_OVERLAY=1`). Hardware: RTX 4060 Ti,
  3440x1440 @ 164 Hz, back buffer 3392x1440 (primary work area). Evidence:
  - Renders: red translucent band and DirectWrite text drawn over the live video, taskbar above
    it. First draw logged `D2D1_ALPHA_MODE_PREMULTIPLIED`; no draw failure logged.
  - Kept rendering across the 400 ms watch tick for the whole run (> 1 min), no failure.
  - GPU 3D, same Debug build, visible desktop: overlay off 2.08 / 1.55 / 2.31 %, overlay on
    2.40 / 1.56 / 2.12 %. The overlay's cost is below the measurement noise. (The long-running
    Release instance read 6.15 / 6.69 / 6.55 % before the run; not the same build or state.)
  - Under the desktop icons: not verifiable, no icons are shown on this desktop.
  - **Explorer restart: BLOCKED by a pre-existing base bug.** After `Stop-Process explorer`, the
    video is gone in both runs, with the overlay on AND with it off (control). Window tree:
    `CosmicWinVideoWallpaperHost-*` is left as an orphaned top-level window, invisible,
    1x1, never re-parented to the new Progman. So the device re-creation path of the overlay
    could not be exercised. Not caused by the spike: with the variable unset `Draw()` returns on
    a bool.
  **Passed, 2026-09-23**, after the base bug was fixed on `fix/explorer-restart-reattach`
  (`322b8fb`, host recreated on the same D3D11 device). Spike branch with the fix cherry-picked
  (`13ff3ac`), overlay on, Explorer killed: new host `0x8046C` child of the new Progman
  `0x605C6`, 3392x1440, visible; the capture shows the video AND the overlay band drawn again;
  the log holds only the first-draw line, no failure. The device survives, so what this exercised
  is the overlay's back-buffer-keyed target rebuild. This feature branch is now stacked on
  `fix/explorer-restart-reattach`.
- [x] **T1 — `AlertCommandParser`.** `CosmicWin.App/Alerts/{AlertKind,AlertCommand,AlertCommandParser}.cs`,
  tests in `CosmicWin.App.Tests/Alerts/AlertCommandParserTests.cs`. Route: delegated writer
  (trigger: 2+ non-trivial files -- parser + shared record/enum types + tests). TDD: RED-1 (type
  missing) `error CS0234: The type or namespace name 'Alerts' does not exist`; stubbed
  `AlertCommandParser.Parse` to throw `NotImplementedException`, RED-2 27 failed / 0 passed, all
  `NotImplementedException`; GREEN after the real implementation, 27 passed / 0 failed. Grammar:
  whitespace-separated `kind:count` tokens (`warning`/`failed`, case-insensitive, count 1..16),
  optional `duration:seconds` (1..60, default 5s), each key at most once, at least one
  warning/failed group required, total tiles <= 16, input capped at 256 chars, order preserved,
  never throws -- bad input returns `AlertCommandParseResult.Fail(message)` naming the offending
  token. Commit `72e9397`.
- [x] **T2 — `AlertQueue`.** `CosmicWin.App/Alerts/AlertQueue.cs` (also defines `ActiveAlert`),
  tests in `CosmicWin.App.Tests/Alerts/AlertQueueTests.cs`. Route: delegated writer (trigger: 2+
  non-trivial files -- the queue plus its test file). TDD: RED-1 (type missing) `error CS0246: The
  type or namespace name 'AlertQueue' could not be found`, 13 occurrences; stubbed
  `AlertQueue.Enqueue`/`Advance` to throw `NotImplementedException`, RED-2 13 failed / 0 passed,
  all `NotImplementedException`; GREEN after the real implementation, 13/13 passed, full
  `CosmicWin.App.Tests` suite 960 passed / 0 failed / 6 skipped (pre-existing hardware-only skips).
  Implements the maintainer's covered-desktop decision above: bounded FIFO (capacity 8 default,
  constructor parameter), `Enqueue` rejects the NEW command when full and reports the rejection;
  `Advance(now, desktopVisible)` ends the current alert once `now >= StartedAt + Duration` (so it
  ends exactly at that instant, not one tick later), drops any queued alert that has waited
  strictly longer than the max age (5 min default, also a constructor parameter) regardless of
  visibility, and starts the next eligible one only when nothing is currently showing AND the
  desktop is visible -- so a covered desktop lets an already-showing alert's clock keep running
  (it ends on time, never paused/extended) but never starts a new one, and back-to-back alerts can
  start on the same `Advance` call the previous one ends on. Every rejection and every drop goes
  through an `Action<string>? onDiagnostic` constructor parameter, defaulting to a no-op -- the
  same optional-delegate convention `AppComposition`'s `persistX` parameters already use, chosen
  over a new interface since the message is a single short string. Documented as not thread-safe;
  the caller (a single-threaded render tick) serializes every call. Commit `2605623`.
- [x] **T3 — `AlertTileLayout`.** `CosmicWin.App/Alerts/AlertTileLayout.cs`, tests in
  `CosmicWin.App.Tests/Alerts/AlertTileLayoutTests.cs`. Route: delegated writer (trigger: 2+
  non-trivial files -- layout algorithm + its test file, alongside the T1 files already in the
  same task). TDD: RED-1 (type missing) `error CS0246: The type or namespace name 'Rectangle'
  could not be found`; stubbed `AlertTileLayout.Layout` to throw `NotImplementedException`, RED-2
  154 failed / 0 passed, all `NotImplementedException`; GREEN after the real implementation, 154
  passed / 0 failed (one REFACTOR-stage correction: a hand-computed expected pixel width in the
  N=1 test was arithmetically wrong -- 22112/9 floors to 2456, not 2457 -- caught by the failing
  assertion and fixed in the test, not the code). Full `Alerts` suite after both tasks: 181
  passed / 0 failed. Algorithm: tries every column count 1..N (rows = ceil(N/cols)), keeps the
  grid whose forced cell yields the largest 16:9 tile (tie -> fewer rows); outer margin and
  inter-tile gap both equal round(2% of the shorter area side); tiles fill row-major in command
  order, the grid is centered in the area, and a partial last row is centered under the rows
  above it. Reuses `CosmicWin.Interop.Rectangle` for the output type rather than adding a new
  one, matching `BorderGeometry`'s existing precedent of App-side pure geometry code depending on
  Interop's Win32-free `Rectangle`. Commit `4d5ffae`.
- [ ] **T4 — Named pipe server and `--alert` client**
- [ ] **T5 — `IFrameOverlay` seam in the player**
- [ ] **T6 — `Direct2DAlertOverlay`**
- [ ] **T7 — Alert visuals ported from great-sage**
- [ ] **T8 — Wiring and `alerts-enabled` setting**
- [ ] **T9 — Supervised hardware run**

Task details and checks: plan §6.

## Progress

2026-09-23: branch `feat/live-alert-wallpaper` created, T0 started. Engram mirror `odd/live-alert-wallpaper/tasks`: **pending** (save refused, several active sessions matched).

2026-09-23: T0 partial: Direct2D on the back buffer works and costs nothing measurable; the
Explorer-restart leg is blocked by a pre-existing re-attach bug (host orphaned).

2026-09-23: base bug fixed (`fix/explorer-restart-reattach`); T0 passed. Branch rebased onto it.

## Reviews

- Slice fix + T1 + T3 (`--base-ref fadfda7`, 1373 lines, risk `medium`, `slice_budget_reached`):
  consent granted by the maintainer; lineage `review-cd5abe02fe1d9247`, one lens
  (reliability), **approved**, acknowledged, authority burned. Reviewed boundary is now `e87a108`.
  Advisory, non-blocking findings (follow-up work, not reopened):
  - `R3-device-rcw-release` (WARNING, inferential): a second Explorer restart could lose the
    device. Checked on hardware the same day: three consecutive Explorer restarts, the host was
    recreated under each new Progman (`0xC0412`, `0x509E0`, `0x50A0A`) and the video kept
    playing. Not reproduced.
  - `R3-concurrent-swapchain-release` (WARNING, inferential): swapchain released on the wallpaper
    thread while the player may be mid-tick. Not observed in four recoveries; no test covers it.
  - `R3-layout-fallback-out-of-bounds` (SUGGESTION): the zero-size fallback grid can go out of
    bounds for counts far above 16; unreachable through the parser (max 16).

## Next step

T4 (named pipe and client).
