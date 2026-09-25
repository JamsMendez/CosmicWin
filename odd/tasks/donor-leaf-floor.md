# Donor leaf floor

## Objective

A resize (keyboard chord or mouse edge drag) must never shrink any WINDOW inside the donor side
below 200 px tile length along the resized axis.

## Problem

`LayoutTree.TransferAcross` (`CosmicWin.Layout/LayoutTree.cs`) protects the donor with
`minRatio` (10% of the ancestor group) and, on the keyboard path only, with the windows' own
minimum sizes (`ActionExecutor.LimitsOfNode`). The ratio protects only the IMMEDIATE sibling, which
is often a GROUP; the leaves inside it are unprotected, and a terminal's own minimum is tiny
(Alacritty: 32). Measured: four windows, two terminals left at 150 px.

## Decision (maintainer, 2026-09-25)

Fixed floor per window: 200 px tile length. Chosen over a ratio of the work area (too restrictive
on an ultrawide) and over leaving it as is.

## Design

- The floor lives in `LayoutTree` (pure), inside `TransferAcross`, so the keyboard chord AND the
  mouse drag (`ApplyEdgeDrag`, which passes no `limitsOf`) obey the same rule.
- Layout knows no gap; the floor is in SLOT lengths: 200 tile + 8 gap (`TreeArranger.DefaultGap`)
  = 208 slot, as a documented Layout constant.
- Inner sizes rescale PROPORTIONALLY (`RescaleSizes`), so the donor's floor is the proportional one:
  leaf -> 208; group on the resized axis -> max over children of `childFloor * groupSum / childSize`
  (a child of size 0 contributes nothing / is skipped safely); group across the axis -> max of the
  children's floors. Rounded up.
- A donor already under its floor refuses to give anything (transfer <= 0); it is never made worse.
- Out of scope: `GrowWithinGroup` (a constrained window claiming its own minimum); `minRatio` stays.

## TDD

Mode: enabled (session configuration, Strict TDD). Runner: `dotnet test`.

## Tasks

- [x] T1 Leaf floor in `TransferAcross` + Layout tests (RED then GREEN), existing suites green.
  Route: delegated writer (2 non-trivial files: LayoutTree.cs + a new test file). Commit 77c3d39.
  - Writer RED was compile-only (missing constant). Behavioural RED by parent mutation (floor line
    neutralised): 4 of the 5 new tests failed (keyboard nested group, mouse drag, across-axis donor,
    already-under-floor no-op); the plain-transfer regression test passes either way, as intended.
  - 4 existing tests rescaled (fixtures smaller than two 208 slots), each keeping the 10% ratio as the
    binding bound it exercises: ResizeNode headroom, ResizeNode rounded step/ceiling, ApplyEdgeDrag
    headroom, TilingEngineContract smoke.
  - Suites: Layout 195, Alert 13, Interop 315/42 skipped, App 958/6 skipped; build 0 errors.
- [x] T2 Hardware check: four windows, resize chord and mouse drag cannot push a window under 200.
  Route: inline (scripted run, no source change). Four WinForms probes (MinimumSize 32x32, a
  terminal-like floor) on a fresh virtual desktop, 3440x1392 work area; Ctrl+Alt+H/L/K/J x15 at leaf
  and group scope, two passes, then right/bottom edge drags of +/-3000 px on every probe.
  - Base (main 2132eef): smallest tile 122 wide, 136 high (keyboard 122/136; mouse bottom drag 136).
  - Fix (bf429c0): smallest tile 200 wide, 200 high on both paths; every tile started >= 708.
  - First attempt used Windows Terminal windows as fillers and killed the Claude session, which runs
    inside the same WindowsTerminal.exe. Fillers are now the agent's own probes, stopped by PID.
  - A mouse grab at the DWM frame edge minus 2 px lands in the client area and resizes nothing; the
    resize border is outside the visible frame (grab at edge plus 3 px).

## Acceptance criteria

- No resize chord or edge drag leaves a tile under 200 px on the resized axis when it started at or
  above 200.
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.

## Progress

T1 and T2 done. Review of the branch against main (base-diff, committed only, 6 files, 383 lines):
risk medium, consent granted, one reliability lens, APPROVED and acknowledged (lineage
`review-999b732e45e35cd6`, authority burned). Merged into local main.

Follow-up (non-blocking, R3-001): the floor tests cover only positive growth with the donor on the
right, one nesting level, horizontal axis. Worth adding: one shrink-direction test (focused side is
the donor) and one two-level mixed-axis (vertical) test.

- [x] T3 R3-001 coverage, branch `test/donor-floor-coverage`. Route: inline (one test file, no
  production change). Added: role swap via chord and via mouse drag (focused nested group is the
  donor, [902, 1098]), and a vertical three-level same/across/same donor ([1336, 1664], deepest leaf
  exactly 208). Green on the existing code, so proven by mutation of `LayoutTree.cs`:
  - floor read from `neighborIndex` instead of the donor role: only the 2 role-swap tests fail (the
    previous suite let this through);
  - across-axis group floored at one leaf slot: only the mixed-axis test fails;
  - same-axis floor without the proportion: 5 of 8 floor tests fail.
  - Layout suite 198 passed.
