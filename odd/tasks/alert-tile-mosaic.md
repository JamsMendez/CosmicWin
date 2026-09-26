# Alert tile mosaic: one tile per failed/warning, laid out like tiled windows

## Objective

An alert command `failed:X warning:Y` shows X + Y tiles instead of one full-screen layer. Each
tile is laid out like a tiled window, with the same outer and inner gap the tiling engine uses.

## Problem

Today the whole alert is one full-screen canvas: `AppComposition` collapses the command to a single
kind (`failed` if any failed group exists, `AppComposition.cs` ~706) and the page draws one layer.
The per-kind counts the parser already accepts (`AlertGroup.Count`) are thrown away.

## Decisions (maintainer, 2026-09-26)

1. Total tiles N = failed + warning, capped at 8 visible slots.
2. Grid by N: 1 -> 1x1 (full display, as today); 2 -> 2 columns x 1 row (50% each);
   3-4 -> 2x2; 5-6 -> 3x2; 7-8 (and more) -> 4x2. Slots are filled row-major (top row left to
   right, then bottom row). Unused slots keep their space and draw nothing (transparent).
3. Order: every failed tile first, then warnings. Past 8 slots the rest is dropped, so failed wins
   (8 failed + 1 warning shows 8 failed, no warning).
4. Gap: the SAME `gap` setting from `settings.conf` (`TreeArranger.Gap`, default 8) for both the
   outer margin and the space between tiles, like tiled windows.
5. Implementation choice (agent): one preloaded WebView2 page draws N tiles. Not N WebViews.
6. Parser grammar unchanged (per-kind 1..16, total <= 16). The display clamps to 8, the command is
   not rejected for asking 9..16.

## Approach

- Pure C# layout: `AlertTileLayout` (App/Alerts) turns an `AlertCommand` into an ordered list of
  tile kinds (failed first, capped at 8) plus the grid (columns, rows). Unit-tested.
- Composition/controller: the show request carries the tile list, grid and gap instead of one
  kind string. `startAlertLayer` / `AlertLayerPreloadState` / `PostShow` carry it; the web message
  becomes `{type:"show", tiles:[...], columns, rows, gap, duration}`. Shake still triggers when any
  failed tile is shown.
- Page (`alert-layer.js`): compute each slot rect with outer + inner gap (gap is physical pixels;
  convert with `devicePixelRatio`), draw each tile's layer clipped/scaled to its rect. Hash API
  gains `tiles=failed,warning&columns=..&rows=..&gap=..` for manual checks in Edge; the old
  `#kind=` form keeps working as a single tile.

## Constraints

- Strict TDD, runner `dotnet test`. Mutation-check any test that passes on its first run.
- JS has no test harness in this repo: page changes are proven on hardware (task T4) and by the
  C# contract tests on the posted message.
- Everything stays local: no push, no gh. No AI attribution in commits.

## Tasks

- [x] T1 `AlertTileLayout` pure layout (tile order, cap 8, grid table) + tests. Route: delegated
  (writer trigger: T1-T3 touch 5+ non-trivial files). Commit `0e73de0`.
- [x] T2 Carry tiles/grid/gap through composition -> preload state -> controller -> web message;
  gap read from `TreeArranger.Gap`; traces name the tiles. Tests updated/added. Route: delegated.
  Commit `1ebf44d`.
- [x] T3 `alert-layer.js`: per-tile rects with outer/inner gap, draw each tile, hash API. Route:
  delegated. Commit `c106aeb`.
- [ ] T4 Hardware: HTTP alerts 1 failed; 2 failed; 3 failed; 5 mixed; 8 failed + 1 warning; gap
  change in settings reflected. Route: inline (drive the app).

## Acceptance criteria

- 1 tile: identical to today (full display).
- N tiles follow the grid table with the settings gap outside and between tiles; empty slots blank.
- Failed tiles always precede warnings; never more than 8.
- All suites green.

## Progress

- 2026-09-26: branch `feat/alert-tile-mosaic` created from main 174ea0f. TDD strict, source:
  project convention (previous ODD features), runner `dotnet test`. Delivery strategy: ask-on-risk.
  Forecast ~350-450 authored lines.

- 2026-09-26: T1-T3 implemented by one delegated writer.

  **T1 -- `AlertTileLayout`** (`CosmicWin.App/Alerts/AlertTileLayout.cs`, new). A pure record
  `AlertTileLayout(Tiles, Columns, Rows)` with a static `From(AlertCommand)` factory: every failed
  tile first, then warnings, capped at 8; grid from the maintainer's table (1->1x1, 2->2x1, 3-4->2x2,
  5-6->3x2, 7+->4x2). Tests: `CosmicWin.App.Tests/Alerts/AlertTileLayoutTests.cs` (new, 15 cases --
  every grid-table row, ordering, the 8-cap with a partial/total cut, 16-total cap regardless of
  command order). RED: 8 `CS0103` compile errors (`AlertTileLayout` did not exist) before the type
  was written. GREEN after: 22/22 (15 new + 7 in the file below). Mutation-checked: (a) broke the
  3-4 grid-table row to `(2,1)` -> 3 tests failed as expected, reverted; (b) swapped the
  failed/warning fill order -> 4 tests failed as expected, reverted.
  <br>Collateral fix: `remove-direct2d-alert-overlay`'s `RemovedDirect2DAlertOverlayTests` used to
  assert `CosmicWin.App.Alerts.AlertTileLayout` stays absent (an EARLIER, unrelated Direct2D-era
  type of the same name). Rewrote that one test (`AlertTileLayout_IsTheNewLiveMosaicLayoutNotThe
  RemovedDirect2DOne`) to assert the new type's shape instead of the old type's absence, with a
  remarks paragraph explaining the reuse -- not silently dropped.

  **T2 -- carry tiles/grid/gap end to end.** New `AlertShowRequest(Tiles, Columns, Rows, Gap,
  DurationMilliseconds)` record (`CosmicWin.App/Alerts/AlertShowRequest.cs`). `AlertLayerPreloadState.
  RequestShow`/`ApplyPendingShowIfDue` now take/return `AlertShowRequest` instead of `(string Kind, int
  Duration)`; `WebViewAlertLayerController.Start(AlertShowRequest)` validates it (tiles non-empty and
  each "warning"/"failed", columns/rows positive, gap non-negative, duration positive) and posts
  `{type:"show", tiles:[...], columns, rows, gap, duration}`. `AlertLayerTrace.Show`/
  `PendingShowApplied` reworded to `tiles=... grid=CxR gap=G duration=...` /
  `... remaining=...`. `AppComposition.UpdateAlertOverlay` now calls `AlertTileLayout.From
  (active.Command)`, maps kinds to wire strings, and reads `TreeArranger.Gap` AT SHOW TIME (so a
  settings change applies to the next alert, not the current one).
  <br>Tests: rewrote `AlertLayerPreloadStateTests.cs`, `AlertLayerTraceTests.cs` (also strengthened
  two cases to an asymmetric grid, 4x2/3x2, after a mutation revealed the original 1x1/2x2 cases
  couldn't detect a columns/rows swap), `WebViewAlertLayerControllerTests.cs`, and every
  `startAlertLayer` wiring fixture (`WebViewAlertCompositionWiringTests.cs`,
  `HttpAlertCompositionWiringTests.cs`, `AlertDesktopVisibilityWiringTests.cs`,
  `AlertHttpEndToEndTests.cs`) for the new signature -- kept the same `"start:{kind}:"`-style prefix
  every existing assertion already greps for, and rewrote the two assertions that depended on the
  OLD single-collapsed-kind behavior (`CombinedCommand_StartsFailedOnceAndEndsAfterDuration`,
  `AlertHttpEndToEndTests.SingleAlert_Answers202OkAndShows`) to the real new tile list, with a
  comment explaining why. Added `MultiGroupCommand_ThreadsTheFullTileListGridAndGapToTheLayer` and
  `CommandPastTheEightTileCap_DropsWarningAndUsesTheEightPlusGrid` as new composition-level coverage.
  <br>RED: 3 `CS0246` compile errors (`AlertShowRequest` did not exist) before the record was
  written. First GREEN attempt surfaced 4 REAL behavioral failures (not just the RED I expected) --
  4 old assertions actually encoded the single-kind contract (`start:warning:` no longer matches
  `start:warning,warning:2x1:...`) and 2 of my own new tests forgot the `shake:120` event that
  precedes a failed tile's start; fixed all 6, then GREEN: 173/173 in the Alerts folder. Mutation-
  checked: (a) hardcoded gap to `0` in `AppComposition` -> the new gap-threading test failed,
  reverted; (b) skipped the remaining-duration recompute in `ApplyPendingShowIfDue` -> 3 tests
  failed, reverted; (c) swapped `"failed"`/`"warning"` in the kind-to-wire-string mapping -> 14
  tests failed, reverted; (d) swapped columns/rows in `AlertLayerTrace.Show`'s format string -> 0
  tests failed (the gap just described), strengthened the two trace tests to an asymmetric grid,
  reran the mutation -> 1 test failed as expected, reverted.

  **T3 -- `alert-layer.js`.** Tile-local coordinate model: `W`/`H` (read by every existing drawing
  function) now hold the CURRENT tile's CSS size instead of the whole canvas'; `canvasW`/`canvasH`
  hold the canvas' own size; `renderTile(rect, kind, progress, ms)` sets `W`/`H` to the tile's size,
  clips+translates `ctx` to the tile's rect, and calls the existing `drawFailureLayer` unchanged
  inside it. `tileRects()` computes each slot's CSS rect: N<=1 returns the whole canvas with NO
  outer gap (so a single tile is byte-for-byte the old full-screen page); N>1 applies the gap as
  outer margin AND inter-cell spacing, row-major. Gap arrives in PHYSICAL pixels and is divided by
  `canvasScaleX`/`canvasScaleY` -- the same devicePixelRatio-derived factors `resize()` already uses
  to size the canvas -- to get CSS pixels. `failureLayer()`'s offscreen buffers and `drawPixelated`'s
  pixelation buffer + final blit now size/position against the current tile's physical rect
  (`tileDeviceX/Y/W/H`) instead of the whole canvas, since `drawPixelated` resets `ctx`'s transform
  before its blit and previously assumed the offscreen buffer's logical size always equalled the
  whole canvas. Shake/reveal state moved from one global state machine (`failureState`/`failureKind`/
  `failureStartMs`) to one entry per KIND (`kindState.failed`/`kindState.warning`), since a mosaic
  can show both kinds at once, each with its own `shakeMs`/`revealMs` theme; `advanceKindState(ms,
  kind)` replaces `advanceFailureState(ms)`. The host message and hash API both grow
  tiles/columns/rows/gap; the old single-tile `{kind}` message and `#kind=` hash form still work
  (trivial back-compat branches).
  <br>No DOM/canvas test harness exists in this repo (stated honestly, per constraints) --
  `alert-layer.js`'s exact pixel output is unverified until the T4 hardware check. Verified instead
  by: (1) `node --check` (syntax only); (2) a throwaway Node `vm`-sandboxed smoke harness (mock
  document/canvas/window, not part of the repo, not committed) that loaded the real file and drove
  `startShowing`/`render`/the message bridge through N=1, 2, 3, 5, 8-tile mixed-kind cases plus the
  old single-kind message, confirming no runtime exceptions and that `"done"` fires correctly for
  every case; (3) a numeric inspection of `tileRects()` for a 3x2/gap=12/dpr=1.25 case and the N=1
  case, confirming the outer/inner gap arithmetic and the "N=1 gets the full canvas, no gap" rule;
  (4) 5 new structural presence checks added to `AlertLayerWebPageTests.cs`, matching this file's
  existing convention, plus all 16 pre-existing structural checks in that file still pass unmodified.

  **Final verification (whole solution):**
  - `dotnet build CosmicWin.sln`: succeeded, only the known pre-existing warnings (2 CS8604/CS8602
    in `MultiMonitorWorkspaceAdapter.cs`, 1 CA2022 in `CosmicWinAlert.Tests/ProgramTests.cs:200`).
  - `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198; `CosmicWinAlert.Tests` 13/13;
    `CosmicWin.Interop.Tests` 384 passed/42 skipped; `CosmicWin.App.Tests` 1050 passed/6 skipped
    (was ~1020/6 at the branch point; +30 net from the new/rewritten Alerts tests).
  - Status: **done** for T1-T3. Commits: `0e73de0` (T1), `1ebf44d` (T2), `c106aeb` (T3).

## Next step

T4 (hardware): drive the app with HTTP alerts -- 1 failed; 2 failed; 3 failed; 5 mixed; 8 failed + 1
warning; a `gap` setting change reflected on the next alert. Manual Edge check available too, e.g.
`alert-layer.html#tiles=failed,failed,warning&columns=2&rows=2&gap=8&duration=5000`.

- 2026-09-26: review `review-ceb16bfac752e1c3` (medium, reliability, main..d48507b) APPROVED and
  acknowledged (authority burned). Non-blocking follow-ups:
  - R3-js-mosaic-behavior-unproved (WARNING): page mosaic math only covered by substring tests;
    suggests a committed vm-sandbox test of `tileRects()` (N=1, 3x2 with gap).
  - R3-negative-gap-blocks-alert (WARNING): `Start` throws on `Gap < 0`; clamp at the call site.
  - R3-hash-tiles-filter-noop (SUGGESTION): hash `tiles=` filter runs after the map; cap at
    columns*rows.
- 2026-09-26: T4 on hardware, Release build of d48507b (PID 42316), on a temporary empty virtual
  desktop (Win+Ctrl+D, closed with Win+Ctrl+F4). HTTP 202 for all; traces:
  `tiles=failed grid=1x1`, `failed,failed grid=2x1`, `failed x3 grid=2x2`,
  `failed x3,warning x2 grid=3x2`, `failed x8 grid=4x2` (8 failed + 1 warning: warning dropped), all
  `gap=8`, done/hide after 4 s each. Screenshots match the grid table, failed-first order, blank
  empty slot, gap outside and between tiles.
  FINDINGS: (1) there is NO `gap` key in settings.conf -- `TreeArranger.Gap` is fixed at
  `DefaultGap` (8) by `AppComposition` (~1756); `gap = 12` in `SettingsTests` is an ignored unknown
  key. The mosaic uses the same gap as tiled windows, but it is not user-configurable; the
  "gap change in settings reflected" check cannot run. (2) The layer spans the full monitor bounds,
  not the work area: with the taskbar on the right, the rightmost column slides partly under it.
  T4 left open pending the maintainer's decision on both.
