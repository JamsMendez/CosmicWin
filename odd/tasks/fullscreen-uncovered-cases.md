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

- [ ] U1 A window admitted already fullscreen is not repositioned, is recorded as fullscreen (same
  trace line), and lands on its tile once it leaves fullscreen. RED first.
- [ ] U2 A neighbour opening or closing does not reposition a fullscreen window (its SetPosition
  count is unchanged); the neighbour still reflows. RED first.
- [ ] U3 A window fullscreen on a non-owner display is recognized (not repositioned, not given up
  on). Two fake displays. RED first.
- [ ] U4 Hardware check driven by the agent: Chrome with its own `--user-data-dir`, F11 plus probe
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
- Next: U1-U3 (one writer), then U4.
