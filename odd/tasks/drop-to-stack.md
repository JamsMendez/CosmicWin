# Drop to stack

## Objective

Dragging a tiled window onto the top or bottom third of another tile stacks it above or below
that tile (the target's slot splits vertically, half each). Dropping on the middle third keeps
today's swap.

## Problem / why

- Today any drop onto another tile swaps the two windows (`TrySwapOnDrop`,
  `CosmicWin.App/MultiMonitorWorkspaceAdapter.cs`). There is no way to move a window INTO another
  column by mouse.
- Reported case: columns `[A] [B] [C over D]`. Dragging D onto the lower part of B should give
  `[A] [B over D] [C]`: B's column splits in two, and C takes the whole height its group left.
- The swap remarks already named this as "a separate decision": it needs a prune of the vacated
  group and an edge concept. The maintainer chose zones (top third = stack above, bottom third =
  stack below, middle = swap) on 2026-10-22.

## Scope

- Layout: a Win32-free tree operation that detaches a leaf (pruning/collapsing the group it
  leaves) and inserts it above or below a target leaf, splitting the target's slot in half.
- App: classify the drop by where the dropped window's centre lands inside the target tile
  (top third / middle / bottom third) and route to stack or swap.
- Docs: README/remarks that describe drag-to-swap.

## Constraints

- Up/down only; left/right zones are not requested.
- Same aim as the swap: the dropped window's centre (no cursor interop).
- Dropping outside every tile or on its own tile behaves exactly as today.
- No size outside the target slot and the vacated group changes.

## Tasks

- [x] **T1 — layout insert-beside operation + tests.** Route: delegated writer (2+ non-trivial
  files: LayoutTree + tests). Check: RED then GREEN in `CosmicWin.Layout.Tests`.
- [ ] **T2 — drop-zone routing in the adapter + tests + docs.** Route: same delegated writer.
  Check: adapter tests for top/middle/bottom drops; headless suite
  `dotnet test CosmicWin.sln -c Release --filter "Category!=RequiresDesktop"` green.
- [ ] **T3 — hardware check.** Route: inline (drive the app). Check: the reported 4-window case
  on the real desktop.

## Acceptance criteria

- `[A] [B] [C over D]`, D dropped on B's bottom third -> `[A] [B over D] [C]`; top third ->
  `[A] [D over B] [C]`; middle -> D and B swap.
- Dropping inside the same column (e.g. D on C's top third) reorders without corrupting the tree.
- Existing swap/drag tests stay green.

## Delivery

Strategy: `ask-on-risk`. Forecast ~350 authored changed lines, one PR slice.

## Progress

- 2026-10-22: branch `feat/drop-to-stack` created from `main` a751ead.
- T1 done (delegated writer): `LayoutTree.StackBeside(moving, target, StackSide)` + `StackSide`
  enum; 10 facts in `LayoutTreeStackBesideTests`. RED against a `=> false` stub: 6 failed / 4
  passed (the refusal facts). GREEN: Layout.Tests 208/208. Headless suite 208 + 240 (3 skipped) +
  944 passed. Build and test ran with `--artifacts-path` in the session scratchpad because a
  running `CosmicWin.App.exe` (PID 3916) locks `CosmicWin.App/bin/Release`; not killed.
  Design: the target is re-read AFTER the detach (a collapse can move it); a target already in a
  vertical group gets a sibling (no extra nesting), otherwise it is wrapped; every refusal is
  checked before anything is detached.
