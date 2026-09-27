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
- [x] T4 Hardware: HTTP alerts 1 failed; 2 failed; 3 failed; 5 mixed; 8 failed + 1 warning; gap
  change in settings reflected. Route: inline (drive the app). Grids verified; gap part moved to T6.
- [x] T5 New `gap = N` key in `settings.conf` (maintainer, 2026-09-26): whole pixels 0..64, default 8,
  an unreadable/out-of-range value keeps the default (same rule as the other keys). Drives
  `TreeArranger.Gap` for windows AND the alert mosaic; applied at startup and on settings reload.
  Serialized with a comment. Alert call site clamps `Gap` to >= 0 (review R3-negative-gap-blocks-alert).
  Route: delegated (writer trigger: Settings + composition + tests). Commit `cbed25d`.
- [x] T6 Hardware: `gap = 24`, reload, windows and a 2x2 alert both show the wider gap.
- [x] T7 Mosaic uses the WORK AREA, like tiled windows (maintainer, 2026-09-26): for N > 1 the grid
  (outer gap included) is laid out inside the monitor's work area, read at show time with
  `GetMonitorInfo` (never WinForms `Screen.WorkingArea`, see the taskbar-tracking work), expressed
  relative to the layer surface and sent in the show message. N = 1 stays full display, as decided
  originally ("funciona como ahora"). Route: delegated.
- [x] T9 Behavioral test of `alert-layer.js` layout (reviews R3-js-mosaic-behavior-unproved,
  R3-js-workarea-layout-only-structurally-guarded): a committed Node script that loads the real
  page file in a `vm` sandbox with a mock canvas and asserts `tileRects()`/`gridAreaRect()` output
  (N=1 full canvas; 2x1, 3x2 with gap; work area offset + clamp; degenerate work area = full canvas;
  dpr scaling; `work=` hash param), run from `dotnet test` via an xUnit fact (skipped with a reason
  when `node` is not on PATH). Route: delegated (T9-T12 one writer). Commit `9a6c7fb`.
- [x] T10 Test the work-area catch path (R3-workarea-catch-path-unexercised): a display whose
  WorkArea/Bounds getter throws -> trace `alert-layer-workarea-failed`, all-zero work area, alert shown.
  Commit `a9f53a2`.
- [x] T11 Isolate the gap reload from the exceptions reload (R3-reload-gap-skipped-on-exception-failure):
  a throwing exceptions reload must not skip the gap reload (and vice versa), each failure traced;
  also rename/comment the never-firing `ImmediateScheduler` test double (R3-immediate-scheduler-never-fires).
  Commit `b652f95`.
- [x] T12 Hash API `tiles=` (R3-hash-tiles-filter-noop): filter empty/unknown entries BEFORE mapping,
  cap tiles at columns*rows. Covered by T9's harness. Commit `5a4dced`.
- [x] T8 Hardware: right-side taskbar, 4x2 alert -> last column fully visible, gap to the taskbar
  edge matches the windows'.

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

T6 (hardware): `gap = 24` in `settings.conf`, tray Reload, confirm already-tiled windows AND a 2x2
alert both show the wider gap. This also closes out T4's second open finding (gap change in
settings reflected), which was blocked on T5 until now.

T4's other open finding -- the layer spans the full monitor bounds, not the work area, so the
rightmost column slides partly under a right-docked taskbar -- is still open pending the
maintainer's decision, unrelated to gap.

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

- 2026-09-26: T5 implemented by one delegated writer, closing finding (1) above.

  **Premise check before writing anything.** T5's own text says the gap should apply "at startup
  and on settings reload", and to "follow how other settings like focus-border or tiling are
  applied on reload". `codegraph_explore` plus a direct read of `CompositionRoot.cs` and
  `AppComposition.cs` showed that premise does not hold: the tray's WE-3 "Reload" trigger
  (`CompositionRoot.BuildTrayMenuController`'s `reload` delegate) has only ever re-read
  `exceptions.conf` (`() => exceptions.Reload(loadExceptions())`) -- `SettingsFile.Load()` is
  called exactly ONCE, in `WireProduction`, and focus-border/tiling/border-color are never
  re-read from disk at all; they change only through the tray's own toggle actions, which mutate
  in-memory state and persist it. There was no "settings reload path" to reuse for those three
  keys because none exists. Rather than inventing a brand-new mechanism (forbidden by the task) or
  silently dropping the "applied ... on settings reload" requirement (which T6's hardware check
  depends on), WE-3's Reload trigger itself was extended to also re-read `gap` -- the smallest
  change that makes an existing trigger do one more thing, not a second trigger.

  **`CosmicWin.App/Settings.cs`.** `Settings` gains `Gap` (default `TreeArranger.DefaultGap`), a
  `gap` key constant, `TryReadGap` (0..64, same shape as `TryReadPort`), a `Parse` branch, and a
  `Serialize` block with a comment in the same voice as the other keys.

  **`CosmicWin.App/CompositionRoot.cs`.** `BuildTrayMenuController` gains an optional
  `Action? reloadGap = null`, invoked ALONGSIDE (not instead of) the exceptions reload inside the
  same delegate passed to `TrayMenuController` -- one trigger, two things reloaded. Unset (every
  caller before this parameter existed) leaves Reload exactly as it was.

  **`CosmicWin.App/AppComposition.cs`.**
  - `WireProduction`: `SettingsFile.Load()` moved ahead of the `TreeArranger.Gap` assignment (was
    `TreeArranger.Gap = TreeArranger.DefaultGap;` before `settings` existed), now
    `TreeArranger.Gap = settings.Gap;`. `Wire(...)` is called with a new
    `loadGap: () => SettingsFile.Load().Gap` (a FRESH read per Reload, mirroring `loadExceptions`,
    not the one-time `settings` value).
  - `Wire`: gains an optional `Func<int>? loadGap = null` parameter. `ResumeTiling`'s inline
    "walk every display and arrange it" loop was extracted, unchanged, into a new
    `RearrangeEveryDisplay()` local function -- `ResumeTiling` now calls it instead of repeating the
    loop. A new `ReloadGap()` local function sets `TreeArranger.Gap = loadGap()` and then calls
    `RearrangeEveryDisplay()` ONLY when `tiling` is currently on -- mirroring `ToggleTiling`'s OFF
    branch, which deliberately leaves every window exactly where the layout last put it. `Gap`
    still updates when tiling is off, so it is there the instant tiling resumes, and the alert
    mosaic (which reads `TreeArranger.Gap` at show time regardless of the tiling switch) sees it
    immediately either way. `reloadGap: loadGap is null ? null : () => onOwningThread(ReloadGap)` is
    threaded into the `BuildTrayMenuController` call -- the same `onOwningThread` wrapper
    `ResumeTiling` already uses when the tray click turns tiling back on, because `ReloadGap` can
    equally rearrange trees and reach the overlay through `AfterArrange`.
  - `UpdateAlertOverlay`'s `AlertShowRequest` construction now reads
    `Math.Max(0, TreeArranger.Gap)` instead of the bare static (review R3-negative-gap-blocks-alert)
    -- `WebViewAlertLayerController.Start` throws `ArgumentOutOfRangeException` on a negative `Gap`,
    and nothing stops another caller of the shared static from setting one negative.

  **Tests.**
  - `SettingsTests.cs`: rewrote `CommentsBlankLinesAndUnknownKeys_AreIgnored` (the `gap = 12`
    fixture used to assert `gap` was an ignored UNKNOWN key -- now a genuinely unknown
    `not-a-real-setting = 12`, with a remark explaining why) and added 8 gap facts mirroring the
    `alert-http-port` section's shape: default, every valid spelling (0/24/64), every invalid one
    (-1/65/non-numeric/decimal/blank/missing), last-assignment-wins, serialize round-trip (4
    values), the comment's presence, and independence from neighbouring keys.
  - `CompositionRootTests.cs`: added `BuildTrayMenuController_Reload_AlsoInvokesInjectedReloadGap`
    (both the exceptions reload and `reloadGap` fire on one `Reload()` call) and
    `BuildTrayMenuController_Reload_WithNoGapReloadWired_OnlyReloadsExceptions` (unset `reloadGap`
    changes nothing about today's behaviour).
  - `Alerts/WebViewAlertCompositionWiringTests.cs`: added
    `ANegativeTreeArrangerGap_IsClampedToZeroRatherThanThrowing` (sets the shared static to `-5`,
    restored in `finally` per this file's own convention, sends an alert, asserts no exception and
    `gap=0` in the recorded show event).
  - `GapReloadTests.cs` (new): an `AppComposition.Wire`-level harness mirroring `TilingModeTests`'
    shape, with three facts -- `Reload_AppliesTheNewGapAndRearrangesTiledWindows` (a filling tile's
    bounds move from the old gap's inset to the new one, reusing the exact edge/gap arithmetic
    `TreeArrangerGapTests` already proves), `ReloadWithTilingOff_UpdatesTheGapButLeavesWindowsAlone`,
    and `GapIsNotReReadOnItsOwn_OnlyOnReload`.

  **RED.** `dotnet build` failed with 10 `CS1739`/`CS1061` errors -- `Settings.Gap` did not exist
  (8 call sites across `SettingsTests.cs`), `Wire` had no `loadGap` parameter, `BuildTrayMenuController`
  had no `reloadGap` parameter -- before any production code was written.

  **GREEN.** After implementing the five production changes above: `CosmicWin.App.Tests` filtered
  to the touched files, 166/166. Full solution: `CosmicWin.Layout.Tests` 198/198, `CosmicWinAlert.Tests`
  13/13, `CosmicWin.Interop.Tests` 384 passed/42 skipped, `CosmicWin.App.Tests` 1075 passed/6 skipped
  (was 1050/6 at the branch's T1-T3 point; +25 net from this task).

  **Mutation checks**, each reverted after confirming the expected failure:
  1. `TryReadGap`'s upper bound `<= 64` -> `<= 65`: 2 tests failed as expected
     (`AnInvalidGap_KeepsTheDefaultRatherThanGuessing("gap = 65")`,
     `GapIsReadIndependentlyOfTheOtherSettings`), reverted.
  2. The `Math.Max(0, ...)` clamp at the alert show site removed: 1 test failed as expected
     (`ANegativeTreeArrangerGap_IsClampedToZeroRatherThanThrowing`, string became `gap=-5` instead
     of `gap=0`), reverted.
  3. `ReloadGap`'s `if (tiling)` guard removed: all 3 `GapReloadTests` still passed -- caught a REAL
     gap in the first version of `ReloadWithTilingOff_UpdatesTheGapButLeavesWindowsAlone`, which
     added its window while tiling was ALREADY off, so the window never got a tree leaf at all
     (`TilingModeTests.WithTilingOff_ANewWindowIsLeftWhereItOpened`'s own fact) and the mutation had
     nothing to rearrange either way. Rewrote the fact to tile the window first, then turn tiling
     off (mirroring `WithTilingOff_ADraggedWindowIsNoLongerSnappedBack`), reran against the same
     mutation -> failed as expected (`SetPositionCallCount` went from 1 to 2), reverted the
     production guard back.
  4. `reloadGap?.Invoke()` removed from `CompositionRoot`'s combined reload delegate: 4 tests failed
     as expected (`BuildTrayMenuController_Reload_AlsoInvokesInjectedReloadGap` plus all 3
     `GapReloadTests`, since none of them ever saw `TreeArranger.Gap` change), reverted.

  **README.** `settings.conf` keys are documented inline near their feature, not in one table --
  added `gap` beside the "Tiling" bullet in "What works" (default, range, and that it also drives
  the alert mosaic), and dropped "and the gap" from "the gap is compile-time" in "What it does not
  do yet", now false.

  **Reload path (for T6).** Windows: hand-edit `settings.conf`'s `gap` line, then the tray's
  "Reload" menu item (WE-3) -- the exact same action that already reloads `exceptions.conf`, no
  new UI, no chord. With tiling on, already-tiled windows are rearranged immediately; with tiling
  off, the value is stored and takes effect the next time tiling is turned back on. The alert
  mosaic picks up the new value on its next `warning:`/`failed:` command regardless of the tiling
  switch (it reads `TreeArranger.Gap` at show time, per T2).

  **Verification.**
  - `dotnet build CosmicWin.sln`: succeeded, only the known pre-existing warnings (2 CS8604/CS8602
    in `MultiMonitorWorkspaceAdapter.cs`, 1 CA2022 in `CosmicWinAlert.Tests/ProgramTests.cs:200`).
  - `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198; `CosmicWinAlert.Tests` 13/13;
    `CosmicWin.Interop.Tests` 384 passed/42 skipped; `CosmicWin.App.Tests` 1075 passed/6 skipped.
  - Status: **done** for T5. Commit: `cbed25d`.

- 2026-09-26: review `review-fab54d0470d004ef` (medium, reliability, d48507b..b5ff1e2) APPROVED and
  acknowledged. Follow-ups: R3-gapreload-static-parallel-race (WARNING) is REFUTED --
  `CosmicWin.App.Tests/TestParallelism.cs:11` disables parallelization assembly-wide;
  R3-reload-gap-skipped-on-exception-failure (SUGGESTION: exceptions reload throwing skips the gap
  reload); R3-immediate-scheduler-never-fires (SUGGESTION: rename/comment the test double).
- 2026-09-26: T6 on hardware, Release build of b5ff1e2. `gap = 24` appended to settings.conf, app
  restarted (startup path; tray Reload path covered by `GapReloadTests`). Tiled windows show the
  wider gap (screenshot); 2x2 alert on a temporary desktop traced
  `tiles=failed,failed,warning,warning grid=2x2 gap=24` and shows the wider outer/inner gap.
  settings.conf restored to its original content afterwards, app restarted (default gap 8).
  Still open from T4: the layer spans monitor bounds, the right-side taskbar covers part of the
  last column -- awaiting the maintainer's decision.

- 2026-09-26: T7 implemented (single writer, this session), closing finding (2) above: the N>1
  mosaic is laid out inside the monitor's work area instead of the whole monitor.

  **How the work area is obtained.** `codegraph_explore` located the existing GetMonitorInfo-backed
  tracking this task asked to reuse: `Win32DisplayManager`/`Win32Display` (`CosmicWin.Interop/
  Win32/`) already read `IDisplay.WorkArea` via `GetMonitorInfo`'s `rcWork` and keep it fresh --
  `AppComposition`'s 400ms watch tick calls `refreshDisplays()` (`Win32DisplayManager.Refresh`)
  every tick, updating each `IDisplay` object IN PLACE (`Win32Display.Refresh`), exactly the
  "taskbar moves/auto-hides" case T7 has to handle. No new P/Invoke was added: `UpdateAlertOverlay`
  reads `treeManager.Primary` (a live handle-keyed lookup, `TreeManager.Primary`) fresh on every
  show, exactly like it already does for `TreeArranger.Gap` (T2/T5, "read at show time").
  <br>`Win32VideoWallpaperHost.CreateHostWindow`'s own remarks establish that the host window
  (the layer's surface) is always created and positioned at exactly the primary monitor's
  `rcMonitor` -- so the surface's screen rect is already `IDisplay.Bounds`, and no
  `GetWindowRect`/`GetClientRect` call was needed to get it either.

  **Making it relative.** New pure type `AlertLayerWorkArea` (`CosmicWin.App/Alerts/
  AlertLayerWorkArea.cs`, record struct `Resolve(Rectangle surfaceBounds, Rectangle workArea)`):
  intersects the work area with the surface bounds, expresses the result relative to the surface's
  OWN top-left (so a monitor not at the desktop origin is handled correctly), and collapses to
  `Unavailable` (`0,0,0,0`) when there is no overlap at all or the surface itself is degenerate --
  the fallback signal both the page and `WebViewAlertLayerController` already treat as "lay out on
  the whole canvas", the pre-T7 behaviour. `UpdateAlertOverlay` wraps the read+resolve in a
  try/catch that also degrades to `Unavailable` and traces `alert-layer-workarea-failed` on any
  exception (there should never be one against these plain property reads) -- this alert must never
  fail just because its work area could not be resolved.

  **Threading it through.** `AlertShowRequest` (`CosmicWin.App/Alerts/AlertShowRequest.cs`) gains
  `WorkAreaLeft/Top/Width/Height` (physical pixels, relative to the surface), all defaulted to 0 --
  `AlertLayerWorkArea.Unavailable`'s own shape -- so every pre-T7 call site keeps compiling and
  keeps meaning "whole canvas". `AlertLayerPreloadState`'s pending/shown tracking carries the four
  fields through unchanged via the existing `with` expression (no production change needed there).
  `WebViewAlertLayerController.PostShow` adds a `"workArea":{"left":...,"top":...,"width":...,
  "height":...}` object to the `{type:"show",...}` JSON message, clamping each field to >= 0
  defensively. `AlertLayerTrace.Show`/`PendingShowApplied` gain a `work=L,T,WxH` segment between
  `gap=` and `duration=`/`remaining=` (e.g. `work=0,40,1920x1040`).

  **Page (`alert-layer.js`).** New `workAreaLeft/Top/Width/Height` module state (physical pixels) and
  `gridAreaRect()`: returns the work area converted to CSS pixels with the SAME
  `canvasScaleX`/`canvasScaleY` devicePixelRatio-derived factors the gap already uses, clamped to the
  canvas, or the whole canvas when `workAreaWidth`/`Height` is <= 0 (unavailable/degenerate).
  `tileRects()` (N>1 branch) now lays the outer gap + grid out inside `gridAreaRect()` instead of
  always the whole canvas; N=1 is untouched (returns the whole canvas before `gridAreaRect` is ever
  called). `startShowing` gains an optional `workArea` parameter (`{left,top,width,height}` or
  missing/null, either treated as unavailable); `handleHostMessage` passes `data.workArea` through
  for the tiles message shape (the old single-kind back-compat shape stays without one -- N=1 always
  ignores it). Hash API gains `work=L,T,W,H` (comma-separated physical pixels, tiles form only),
  parsed by a new `parseWorkAreaParam`.

  **Verified without a DOM/canvas harness (stated honestly, per constraints), same split as T3:**
  (1) `node --check` (syntax only); (2) a throwaway Node `vm`-sandboxed smoke harness (mock
  document/canvas/window, not part of the repo, not committed) driving `startShowing`/`tileRects`
  directly through N=1 (work area ignored), N=2 with no work area (matches the exact pre-T7
  full-canvas rects), N=2 with a work area narrowed on the right by 200px (both tiles land strictly
  inside `[0,1720)`, proving the taskbar-spill bug this task fixes is actually gone at the
  arithmetic level), N=2 with a degenerate (zero-width) work area (falls back to the exact
  no-work-area rects), and the `work=` hash param (matches the message-driven result byte-for-byte)
  -- all 8 checks passed; (3) the 5 new/rewritten structural presence checks in
  `AlertLayerWebPageTests.cs`, plus all pre-existing structural checks in that file, still pass.

  **Tests (new/rewritten, C# side).**
  - `AlertLayerWorkAreaTests.cs` (new): 7 facts on the pure resolver -- no taskbar, right-docked
    taskbar, a monitor not at the desktop origin (top-docked taskbar), a work area wider than the
    surface (clamped), a work area entirely outside the surface (falls back to `Unavailable`), a
    degenerate surface (falls back), and `Unavailable` itself being all-zero.
  - `AlertLayerTraceTests.cs`: the two `Show` facts and the one `PendingShowApplied` fact rewritten
    with distinct, asymmetric work-area values (not just the grid/gap already were) so a field-order
    swap among the four new fields would fail them too, same reasoning T2 already applied to the
    grid.
  - `WebViewAlertLayerControllerTests.cs`: the two exact trace-string assertions updated for the new
    `work=0,0,0x0` segment (both calls use the default, unset work area); new
    `PostShowIncludesTheWorkAreaInTheShowMessage` (structural, reads `PostShow`'s body).
  - `AlertLayerPreloadStateTests.cs`: new `PendingShowCarriesTheWorkAreaUnchanged` (passed on first
    run -- the `with` expression already threads unlisted fields through by construction, same as
    Gap already does; documented honestly rather than claiming a RED that could not exist).
  - `WebViewAlertCompositionWiringTests.cs`: `Create()`'s returned tuple now also exposes the fake
    `Display` (its `WorkArea` is settable, mirroring how the real `Win32Display` updates in place);
    `DescribeShow` gains the `work=L,T,WxH` segment between `gap=` and the trailing duration, using
    commas (not colons) inside it so every existing `StartsWith`/`LastIndexOf(':')`-based assertion
    stays valid unmodified. New `ReadsTheWorkAreaAtShowTimeAndThreadsItRelativeToTheSurface`
    (mutates `Display.WorkArea` AFTER `Wire` returns but BEFORE sending the command, proving the
    read is live, not a wire-time snapshot) and
    `UnresolvableWorkAreaFallsBackToUnavailableRatherThanFailingTheAlert`.
  - `AlertLayerWebPageTests.cs`: 2 new structural facts on the JS additions above.

  **RED.** `AlertLayerWorkAreaTests.cs` alone: 14 `CS0103`/`CS0246` (type did not exist). Then, with
  `AlertShowRequest`/`AlertLayerTrace` still unchanged: 3 `AlertLayerTraceTests` failures (no `work=`
  segment yet), 1 `WebViewAlertLayerControllerTests` failure (`PostShow` had no `workArea` key yet),
  1 `WebViewAlertCompositionWiringTests` failure
  (`ReadsTheWorkAreaAtShowTimeAndThreadsItRelativeToTheSurface`, since `UpdateAlertOverlay` had not
  wired the work area through yet -- its sibling unavailable-fallback fact passed by coincidence,
  since the pre-T7 code already always sent the all-zero shape), and 2 `AlertLayerWebPageTests`
  failures (JS additions did not exist yet) -- each observed before writing the matching production
  code.

  **GREEN.** After each production change above: `CosmicWin.App.Tests` filtered to the touched
  files, all green. Full solution: `CosmicWin.Layout.Tests` 198/198, `CosmicWinAlert.Tests` 13/13,
  `CosmicWin.Interop.Tests` 384 passed/42 skipped, `CosmicWin.App.Tests` 1088 passed/6 skipped (was
  1075/6 at the branch's T6 point; +13 net, matching the 13 new facts listed above).

  **Mutation checks**, each reverted after confirming the expected failure:
  1. `AlertLayerWorkArea.Resolve`'s left-clamp axis swapped (`surfaceBounds.Left` ->
     `surfaceBounds.Top`): `MonitorNotAtTheDesktopOrigin_IsExpressedRelativeToTheSurfaceNotTheDesktop`
     failed as expected, reverted.
  2. The no-overlap guard loosened (`right > left && bottom > top` -> `right >= left && bottom >=
     top`): `WorkAreaOutsideTheSurface_FallsBackToUnavailable` failed as expected, reverted.
  3. `UpdateAlertOverlay`'s `AlertLayerWorkArea.Resolve(alertDisplay.Bounds, alertDisplay.WorkArea)`
     arguments swapped: caught a REAL gap first -- the original composition-level test used a
     right-docked, origin-aligned work area, and swapping surface/work-area arguments happens to
     produce the identical result when both rects share the same top-left corner, so the mutation
     passed. Strengthened the test to a TOP-docked (asymmetric) work area instead, reran against the
     same mutation -> failed as expected (`work=0,0,...` instead of `work=0,40,...`), then reverted
     the production swap and confirmed the strengthened test passes GREEN.

  **Status: done for T7.** T8 (hardware: right-side taskbar, 4x2 alert) is still open -- out of this
  session's scope (constraints: do not run/kill the Release app already running from `bin\Release`)
  and needs a supervised run on real hardware with a right-docked taskbar.

- 2026-09-26: review `review-8261e7e19627cd5f` (medium, reliability, b5ff1e2..9149d9f) APPROVED and
  acknowledged. Follow-ups: R3-js-workarea-layout-only-structurally-guarded (WARNING, same class as
  R3-js-mosaic-behavior-unproved: page layout math only guarded by substring tests -- a committed
  Node vm test of `tileRects()`/`gridAreaRect()` would close both); R3-workarea-catch-path-unexercised
  (SUGGESTION: fake display whose WorkArea getter throws, assert trace + Unavailable).
- 2026-09-26: T8 on hardware, Release build of 9149d9f, right-side taskbar, temporary desktop.
  `failed:7 warning:1` -> trace `grid=4x2 gap=8 work=0,0,3392x1440` (monitor 3440 wide, taskbar 48).
  Screenshot: last column fully visible, 8-px gap before the taskbar, warning in the last slot.

- 2026-09-26: T9-T12 implemented by one delegated writer (this session), closing all four review
  follow-ups from the two most recent reviews. Node `v24.19.0` on PATH throughout.

  **T9 -- Node vm-sandbox harness for `alert-layer.js`.** New
  `CosmicWin.App.Tests/Alerts/Web/alert-layer-layout.tests.js`: loads the REAL shipped page (the
  same `AppContext.BaseDirectory`-resolved copy `AlertLayerWebPageTests` already reads) into a
  `vm.createContext` sandbox with a minimal DOM/canvas/host mock (a Proxy-backed 2D context whose
  every method/property is a no-op/slot, `document.getElementById`/`createElement`, `window` sizing
  + `devicePixelRatio` + an optional `chrome.webview`, and `location.hash`), then asserts
  `tileRects()`/`gridAreaRect()`/the hash-parsed module state directly -- top-level `function`/`var`
  declarations in a `vm.runInContext` script already become sandbox properties, confirmed BEFORE
  writing anything, so no export/module system was added to `alert-layer.js`. 12 cases: N=1 (full
  canvas, gap+work area ignored), 2x1 and 3x2 grids with gap (row-major, equal cells), work-area
  offset, work-area clamp to canvas, no-work-area fallback, degenerate (zero-width) work-area
  fallback, `devicePixelRatio=1.5` converting both gap and work area, `tiles=`/`columns=`/`rows=`/
  `gap=`/`duration=` hash parsing, `work=` hash parsing, a malformed (3-element) `work=` being
  ignored, and the old `#kind=` single-tile form. A 13th case (T12, below) followed.
  <br>Discovered mid-write: `assert.deepStrictEqual` reports "same structure but are not
  reference-equal" for a structurally-identical object/array returned FROM the vm sandbox compared
  against an outer-realm literal -- cross-realm `Object`/`Array` prototype identity, not a real
  content difference. Fixed with a `plain(value)` helper (`JSON.parse(JSON.stringify(value))`)
  wrapping every sandbox-origin value before comparison; every value here is a plain number/string,
  so the round-trip is safe.
  <br>Wired into `dotnet test` via new `CosmicWin.App.Tests/Alerts/AlertLayerLayoutNodeTests.cs`
  (shells out to `node <harness> <alert-layer.js path>`, asserts exit code 0, surfaces stdout/stderr
  on failure) and `NodeAvailability`/`RequiresNodeFactAttribute` (new), mirroring
  `DesktopFactAttributes`' constructor-time `Skip` shape -- skips with a clear reason when `node`
  cannot be run, never fakes it. The harness script ships via a new `Content` item
  (`CopyToOutputDirectory`) in `CosmicWin.App.Tests.csproj`, the same mechanism `CosmicWin.App.csproj`
  already uses for `alert-layer.js` itself.
  <br>**RED/GREEN:** not applicable in the usual sense -- T9 is a pure test addition over EXISTING
  (T3/T7) production behavior, so the harness passed 12/12 on its first real run. Mutation-checked
  per the task's own instruction: (1) dropped `area.x` from `tileRects`' per-tile `x` -- the
  dpr/work-area-offset case (the only one with a non-zero `area.x`) failed as expected, reverted;
  (2) swapped `w`/`h` in the pushed rect -- the 2x1, 3x2 and dpr cases (3/12) failed as expected,
  reverted; (3) loosened `gridAreaRect`'s degenerate guard (`<= 0` -> `< 0`) -- the 2x1, 3x2, and both
  no-work-area/degenerate-fallback cases (4/12) failed as expected, reverted. Full `dotnet test` on
  `CosmicWin.App.Tests` after: 1089/1095 passed, 6 skipped (was 1088/6 at the branch's T7+T8 point;
  +1, the one new xUnit fact -- the JS harness's 12 internal cases are not separate .NET tests).
  Commit `9a6c7fb`.

  **T12 -- hash `tiles=` filter (covered by T9's harness).** Added a 13th case to the same harness
  file BEFORE fixing anything: `#tiles=failed,,bogus,warning,failed&columns=2&rows=1` asserting the
  parsed tile list equals `["failed","warning"]`. **RED** confirmed against the still-unfixed
  production file: 12/13 passed, the new case failed with
  `["failed","warning","warning","warning","failed"]` (5 tiles, no cap) -- the old code `.map()`ped
  every entry (including `""` and `"bogus"`) straight to `"failed"`/`"warning"` BEFORE its own
  `.filter(tile.length > 0)`, so the filter never dropped anything. Fixed in `alert-layer.js`: filter
  to exactly `"failed"`/`"warning"` BEFORE mapping, then `.slice(0, columns*rows)`. **GREEN**: 13/13.
  Full solution unaffected otherwise (T12 added no new .NET test, only a JS-level case inside T9's
  existing fact). Commit `5a4dced`.

  **T10 -- work-area catch-path test.** `AppComposition.UpdateAlertOverlay`'s try/catch around
  `AlertLayerWorkArea.Resolve` (~line 744, degrading to `Unavailable` and tracing
  `alert-layer-workarea-failed`) already existed from T7 but had never been exercised by anything
  actually throwing. New `ThrowingWorkAreaDisplay` (`WebViewAlertCompositionWiringTests.cs`) and a
  new optional `primaryDisplay` parameter on that file's `Create()` harness (defaults to the existing
  `FakeDisplay`, so every other fixture is unaffected). First attempt made `WorkArea` throw
  unconditionally -- this broke `AppComposition.Wire` itself, which reads the primary display's
  `WorkArea` ONCE, eagerly, at wiring time (`WorkAreaResolver.Resolve`, `AppComposition.cs` ~278, for
  the initial tiling layout) -- a real gap in the plan caught by actually running the test, not by
  inspection. Fixed by throwing from the SECOND read onward only (first call returns `Bounds`).
  <br>**RED/GREEN:** passed on first run (the catch already existed). Mutation-checked: changed the
  catch's trace message to a literal `"MUTATED ..."` -- the new fact failed as expected
  (`alert-layer-workarea-failed` no longer present), reverted. Commit `a9f53a2`.

  **T11 -- isolate the gap reload from the exceptions reload.** Premise held: `CompositionRoot.
  BuildTrayMenuController`'s Reload delegate ran `exceptions.Reload(loadExceptions())` then
  `reloadGap?.Invoke()` as two statements in a row, so a throwing `loadExceptions()` propagated
  straight out and `reloadGap` never ran. Extracted a private `Reload(exceptions, loadExceptions,
  reloadGap, desktopTrace)` helper: each half now runs in its own try/catch (excluding
  `OutOfMemoryException`/`StackOverflowException`/`AccessViolationException`, the same corruption-
  class exclusion `AppComposition.IsRecoverableAlertLayerFailure` already uses), tracing
  `reload-exceptions-failed`/`reload-gap-failed` on failure. `BuildTrayMenuController` gained an
  optional `IDesktopTrace? desktopTrace` parameter, wired from `AppComposition.Wire`'s existing trace
  sink at its `CompositionRoot.BuildTrayMenuController` call site.
  <br>Also renamed `GapReloadTests`' `ImmediateScheduler` test double to
  `NeverFiringReconcileScheduler` (with a remark explaining why: its stored `_callback` was NEVER
  invoked, only ever discarded -- the old name promised behavior it did not have) and dropped the
  now-pointless field.
  <br>**RED**, confirmed by TEMPORARILY reverting the Reload delegate to the old two-statement form
  and re-running the two new facts (`BuildTrayMenuController_Reload_AThrowingExceptionsReloadStillRunsTheGapReload`,
  `..._AThrowingGapReloadStillRunsTheExceptionsReload`): both failed with the injected exception
  escaping `Reload()` uncaught, exactly the bug the review named. Restored the fix, reran: **GREEN**.
  `CompositionRootTests`+`GapReloadTests` filtered: 17/17. Commit `b652f95`.

  **Final verification (whole solution), this session:**
  - `node --version`: `v24.19.0`.
  - `dotnet build CosmicWin.sln`: succeeded, only the same three known pre-existing warnings (2
    CS8604/CS8602 in `MultiMonitorWorkspaceAdapter.cs`, 1 CA2022 in
    `CosmicWinAlert.Tests/ProgramTests.cs:200`).
  - `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198; `CosmicWinAlert.Tests` 13/13;
    `CosmicWin.Interop.Tests` 384 passed/42 skipped; `CosmicWin.App.Tests` 1092 passed/6 skipped (was
    1088/6 at the branch's T7+T8 point; +4 -- T9's one Node-harness fact, T10's one throwing-display
    fact, T11's two Reload-independence facts; T12 added no new .NET fact). The Node harness fact
    ACTUALLY RAN (not skipped) under `dotnet test`, both before T12's fix (observed RED: 12/13) and
    after (observed GREEN: 13/13).
  - Status: **done** for T9, T10, T11, T12.

- 2026-09-26: review `review-bd6d059ce4a7b711` (HIGH, 4 lenses, 9149d9f..a3f3b3b) APPROVED and
  acknowledged. 11 non-blocking follow-ups, none opened a correction:
  - WARNING R4-reload-swallow-without-trace (`CompositionRoot.cs:162-173`): T11's isolation swallows
    a reload failure without recording it.
  - WARNING R3-t12-filter-fix-not-discriminated (`alert-layer-layout.tests.js:227-232`): the T12 case
    does not tell the filter-before-map fix apart from the old behavior.
  - WARNING R2-node-probe-catch-comment-misleading + SUGGESTION R3-node-probe-leaks-on-timeout
    (`NodeAvailability.cs:42-49`): the catch really guards `ExitCode` after a timed-out wait; a hung
    probe is never killed.
  - SUGGESTION R3/R4-node-harness-no-timeout (`AlertLayerLayoutNodeTests.cs:66-69`): sequential
    stdout/stderr reads + unbounded `WaitForExit()` can hang the test run.
  - SUGGESTION R3-hash-tiles-empty-or-negative-grid-unproved, R4-hash-tiles-empty-list,
    R2-hash-tiles-cap-order-vs-failed-priority (`alert-layer.js:699-704`, manual hash API only).
  - SUGGESTION R2-hardcoded-line-anchors-in-doc-comments, R2-t10-doc-says-bounds-throws.
