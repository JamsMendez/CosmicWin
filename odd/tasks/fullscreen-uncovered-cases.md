# Fullscreen uncovered cases

## Objective

Close the three fullscreen cases left uncovered by the fullscreen-eviction fix (`a023fac`). The
maintainer authorized this on 2026-09-25.

## Current state (mapped 2026-09-25)

- `MultiMonitorWorkspaceAdapter.IsFullscreen(window, display)` (`:1163`, tolerance 2 px). A window
  is fullscreen when it has neither `WS_CAPTION` nor `WS_MAXIMIZE`, and its bounds cover
  `display.Bounds` within 2 px. It has ONE call site: `OnWindowBoundsChanged` (`:1334`), which adds
  the window to `_fullscreen`, traces `fullscreen hwnd=...` once, and returns before the miss
  counter / give-up logic.
- `TreeArranger.Apply` (`TreeArranger.cs:92-137`) calls `SetPosition` on every repositionable leaf,
  with no fullscreen exemption.
- Tests: `CosmicWin.App.Tests/FullscreenWindowTests.cs` (11 tests, one display, windows always
  admitted captioned).

## The three cases

1. **Admitted already fullscreen** (a game that launches fullscreen). `OnWindowAdded` ->
   `AddWindow` never asks `IsFullscreen`, and the arrange that follows shrinks the window to its
   tile. A REAL DEFECT, untested.
2. **A neighbour opens or closes while a window is fullscreen.** The reflow repositions every leaf,
   the fullscreen one included, once per neighbour event. The code comment (`:1330-1333`) accepts
   this, and relies on the app snapping back. Untested. It also flickers, and an app that does not
   snap back stays tiled.
3. **Fullscreen on a monitor other than the owner display.** `IsFullscreen` compares with
   `_owners[handle]`'s `display.Bounds`, not with the monitor the window is actually on. A window
   fullscreen on monitor B but owned by A's tree fails the check. Untested (one fake display only).

## Approach

- Cases 1 and 2 share one rule: a window that IS fullscreen is never repositioned by an arrange.
  Apply it where positions are applied, not only in the bounds-changed handler, so admission and
  every reflow respect it. The leaf keeps its slot in the tree, so the window lands back on its tile
  when it leaves fullscreen (the existing `AWindowThatLeavesFullscreen_LandsBackOnItsTile` must
  still pass).
- Case 3: judge fullscreen against the display whose bounds the window actually covers (any known
  display), not only its owner. Decide in the writer's report whether ownership should also move;
  the default is NO (fullscreen is transient; the tile stays with the owner).

## Tasks

- [x] U1 A window admitted already fullscreen is not repositioned, is recorded as fullscreen (same
  trace line), and lands on its tile once it leaves fullscreen. RED first.
- [x] U2 A neighbour opening or closing does not reposition a fullscreen window (its SetPosition
  count is unchanged); the neighbour still reflows. RED first.
- [x] U3 A window fullscreen on a non-owner display is recognized (not repositioned, not given up
  on). Two fake displays. RED first.
- [x] U4 Hardware check driven by the agent: Chrome with its own `--user-data-dir`, F11 plus probe
  windows opening and closing, and launching Chrome straight into fullscreen (`--start-fullscreen`).
  Second monitor only if one is connected.

## Constraints

- Everything stays local: no push, no gh.
- Strict TDD (runner `dotnet test`). Mutation-check any test that passes on its first run.
- Probe windows are the agent's own, killed by PID. Never touch Windows Terminal.

## Progress

- Branch `fix/fullscreen-uncovered-cases` from local main `85694b1`.
- Item 4 of the pending list (watchdog) is NOT in this feature. The cursor gate it described no
  longer exists; the watchdog is backstop-only (30 min, `7a8f6a9`). The trace shows 7343 reinstalls,
  all `foundGone=0`, so the hook has never been seen dead.
- U1+U2 done, commit `cffbee6` (writer). One rule: `TreeArranger.ArrangeAndPosition`/`Apply` take
  an optional `frozen` handle set; a frozen leaf keeps its slot and computed tile but is never passed
  to `SetPosition` nor measured for eviction. The eviction re-entry passes the set on. The adapter
  routes all 7 of its arrange call sites through one `Arrange(tree, workArea)` helper that passes
  `_fullscreen`. `AddWindow` classifies fullscreen right after `InsertWindow`, traces the same line
  once, and still arranges (so neighbours reflow). Handles leave `_fullscreen` on removal and when
  the window stops being fullscreen. RED: 5 new tests failed on the old code; mutations (drop the
  admission check; drop the frozen skip) each broke their tests, then were reverted.
- U3 done, commit `9962b2b`. `IsFullscreen(window, IEnumerable<IDisplay>)`, checked against every
  known display at both call sites. Ownership does NOT move. RED: the two-display test gave up
  after 12 misses on the old code. Mutation (only the owner display) broke exactly that test.
- Parent review of the diff: the writer lost its uncommitted work to a `git checkout --` and
  rebuilt it; the parent read the whole production diff (frozen passed through the eviction
  recursion, `_fullscreen` exits at `:1263`/`:1407`, no stray edits).
- Checks: build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13,
  Interop 387/42 skipped, App 1008/6 skipped. Assess (base `85694b1`): medium, 360 lines,
  `under_budget`.
- U4 hardware (2026-09-25, driven by the agent, elevated, ONE monitor 3440x1440, A/B on main
  `85694b1` vs branch `9962b2b`). `trace-dialogs` was switched on for the runs and removed after
  (it was off before). Chrome moves were recorded with an `EVENT_OBJECT_LOCATIONCHANGE` hook, not
  sampling. Harness notes: Chrome started elevated hands its window to a CHILD process (match by
  the `--user-data-dir` on the command line); Windows PowerShell 5.1 needs
  `-ExecutionPolicy Bypass` for probe scripts; pass `[NullString]::Value`, not `$null`, to a
  P/Invoke string.
  - Case 1, the REAL reproduction: Chrome F11-fullscreen FIRST, then CosmicWin starts (the startup
    enumeration admits it). main: `added ... [L=0 T=0 W=3440 H=1440] -> [L=8 T=8 W=3376 H=1424]`,
    then `fullscreen` (it tried to tile it; Chrome refused the size). Branch: `fullscreen` FIRST,
    no tile, never positioned. `--start-fullscreen` does NOT reproduce case 1: Chrome is admitted
    windowed first (`W=1667 H=1413`) and goes fullscreen afterwards, the same on both builds. A
    borderless WinForms probe covering the monitor is never admitted at all (no WS_SYSMENU,
    THICKFRAME or min/max boxes). Chrome fullscreen (0x160B0000) keeps WS_SYSMENU + both boxes,
    which is why it is admissible.
  - Case 2, a neighbour opens and closes around F11 Chrome: on both builds Chrome only shows its own
    1 px settle (3440x1440 <-> 3440x1439, the `a023fac` note). But main traces `fullscreen`
    AGAIN after the neighbour opens (the reflow knocked it out of the set); the branch does not.
    Leaving F11 lands it back on its tile on both builds.
  - Case 3: not run, one monitor only; covered by the two-display unit test.
  - Side effect: each CosmicWin start re-tiled the maintainer's own open windows (Discord, a Chrome
    window), as any normal start does.
- Review (2026-09-25): 399 lines (under budget), offered per the maintainer's "always review"
  rule. Maintainer granted. Lens review-reliability, lineage `review-b32fde7cd6524b5b`: APPROVED,
  acknowledged, authority burned. The reviewed boundary is now `5347436`. Both findings were fixed
  with maintainer approval:
  - R3-002 (SUGGESTION): the `Arrange` helper sat between `OnWindowAdded`'s remarks and its
    declaration, so the docs attached to the wrong member. Moved below `OnWindowAdded`, `35a665e`.
  - R3-001 (WARNING), part 1: fullscreen admissions now also trace
    `added hwnd=0x.. class=.. proc=.. [L= T= W= H=] -> left alone (fullscreen)`, `8b11dfa`. RED then
    GREEN; mutation (drop the Record) failed the test.
  - R3-001, part 2, a REAL defect found by the test-first probe (`21dbabe`): the fullscreen early
    return ran BEFORE the floor-on-record check, so a window with a known floor, re-admitted while
    fullscreen, kept a leaf it could not fit. Leaving fullscreen then cost 2 extra SetPosition
    rounds into an overflowing tile before it was parked; the non-fullscreen path costs 0. Fix: the
    floor block (DoesNotFitItsFloor / TryRegroupToFit / TryGrowToFit / Untile) now runs before the
    fullscreen check. That is safe because those helpers only call the pure `TreeArranger.Arrange`,
    and `Untile` goes through the frozen-aware `Arrange` (checked by the parent). RED: `TryGetLeaf`
    was true instead of false. Mutation (`false &&` on the floor check) failed both new tests.
  - Checks: build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13,
    Interop 387/42 skipped, App 1011/6 skipped.
- FEATURE COMPLETE on `fix/fullscreen-uncovered-cases`. Not merged, not pushed.
