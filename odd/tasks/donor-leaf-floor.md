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

- [ ] T1 Leaf floor in `TransferAcross` + Layout tests (RED then GREEN), existing suites green.
  Route: delegated writer (2 non-trivial files: LayoutTree.cs + a new test file).
- [ ] T2 Hardware check: four windows, resize chord and mouse drag cannot push a window under 200.

## Acceptance criteria

- No resize chord or edge drag leaves a tile under 200 px on the resized axis when it started at or
  above 200.
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.

## Progress

(none yet)
