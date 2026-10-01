# Block maximizing while tiling is active

## Objective

While tiling is active, a tiled window cannot be maximized: its title-bar maximize button is
disabled, and a window that maximizes anyway is put back into its tile slot.

## Why

Maintainer request, 2026-09-30: "bloquear el maximizar de las ventanas cuando esté activo el modo
tiling", specifically the title-bar button. Chosen behavior: "Botón gris + respaldo" (strip the
button AND restore any maximize that still happens).

## Current state (mapped from code, 2026-09-30)

- "Tiling active" = `!LayoutIsFrozen()` (`AppComposition.cs`, `hook.IsPaused || !tiling`), passed to
  `MultiMonitorWorkspaceAdapter` as `_isPaused`. Tiling OFF (`setTiling(false)`) deliberately does
  nothing to existing windows today; ON runs `ResumeTiling`.
- No production code writes window styles, calls `ShowWindow`, or reads `IsZoomed`. A maximized
  window stays in the tree; `OnWindowBoundsChanged` ends in `Arrange`, so it is probably already
  snapped back with a flash (hypothesis, not checked on hardware). `:1440` skips the resize
  write-back when `maximized` (Aero-snap guard). A window that keeps fighting can be evicted by
  `Judge` after `MissesBeforeGivingUp`.
- Fullscreen (`IsFullscreen`) = no caption bits, no `WS_MAXIMIZE`, covers a monitor. Maximize
  detection MUST key on `WS_MAXIMIZE`, never on "covers the monitor".
- `WindowFilters` reads `MaximizeBox` for admission (`hasMaximizeBox && hasMinimizeBox` in
  `IsAutoExcluded`): stripping must not change the verdict of an already-admitted window.

## Scope

In scope:
- Strip `WS_MAXIMIZEBOX` (+ `SWP_FRAMECHANGED`) from a window when it is tiled while tiling is
  active; remember its original style per handle.
- Restore the original button when: the window leaves the tree (removed, excluded, untiled,
  fullscreen), tiling is turned off, CosmicWin disposes (normal exit). Re-strip on tiling back on.
- Fallback: a tiled window that reports `WS_MAXIMIZE` while tiling is active (and is not
  fullscreen) is restored (`SW_RESTORE`) and re-arranged into its slot; this must not count toward
  `Judge` eviction.
- README: document the behavior and its limits.

Out of scope: a settings key (behavior follows tiling on/off), persisting stripped handles across a
crash, floating windows, the window manager's own windows.

## Constraints and known limits

- `CosmicWin.Interop` is the only project touching Win32. New native calls go through
  `INativeWindowSource` / `IWindow`; failures never throw (model: `Win32Window.SetPosition` ->
  `CanReposition = false`).
- Elevated windows reject `SetWindowLongPtr`/`ShowWindow` from a non-elevated CosmicWin (UIPI):
  degrade silently, trace it.
- Apps with custom title bars (Chrome, Electron, VS Code, Windows Terminal, UWP) may ignore
  `WS_MAXIMIZEBOX`; the fallback covers them.
- If CosmicWin crashes or is killed, stripped windows keep a disabled button until reopened
  (accepted by the maintainer).
- HWNDs are reused: per-handle state is cleared on removal.

## TDD mode

**Strict TDD: enabled**, source: user's global instructions. Runner: xUnit 2.9.3,
`dotnet test CosmicWin.App.Tests` / `dotnet test CosmicWin.Interop.Tests` (App.Tests takes ~4 min).

## Delivery

Strategy: `ask-on-risk`. Forecast: ~700 authored changed lines (above the ~400 budget); the chain
strategy question is asked before delivery. Everything stays local; the maintainer pushes.

## Tasks

- [x] T1: Interop: `INativeWindowSource`/`IWindow` members to set/clear the maximize box and to
  restore a maximized window (no-throw, UIPI-tolerant); fakes updated. Route: delegated writer
  (trigger: 2+ non-trivial files). Members: `IWindow.TrySetMaximizeBox(bool)` / `TryRestore()`,
  `INativeWindowSource.TrySetMaximizeBox(hwnd, bool)` / `TryRestoreFromMaximized(hwnd)`. Native:
  SetWindowLong GWL_STYLE + SetWindowPos FRAMECHANGED, verified by style read-back; restore is
  `ShowWindow(SW_RESTORE)` (documented to restore a maximized window; activation accepted because
  a maximizing window already holds the foreground). A refused write does not flip `CanReposition`.
  RED: `Win32WindowTests.TrySetMaximizeBox_Clearing_...`, `TrySetMaximizeBox_Setting_...`,
  `TryRestore_ForwardsToNative_...` failed `Assert.True` against stub members returning false
  (3 of 25 failing). GREEN: 25/25. Commit: see Progress.
- [x] T2: Adapter: strip on tile while active, remember original, restore on leave/tiling-off/
  dispose, re-strip on tiling-on. Route: same delegated writer (trigger: 2+ non-trivial files).
  `_boxStripped` (handles whose box we cleared = "originally had one") + `_boxRefused` (trace the
  UIPI refusal once). Strip at end of `AddWindow` and on every settled bounds change (also re-strips
  after fullscreen); restore on removal, exclusion (minimize), `Untile` (floor/Judge eviction),
  fullscreen entry, `CanReposition` loss, public `ReleaseMaximizeBlock()` (tiling OFF, wired in
  `AppComposition.setTiling`), `Dispose`; public `ApplyMaximizeBlock()` re-strips on tiling ON.
  Decision: restore/strip is keyed on the TILING toggle, NOT on `hook.IsPaused` (a brief, reversible
  suspension; flashing every title bar twice for it is noise). Admission verdict preserved by
  `IsExcludedAsAdmitted` (puts WS_MAXIMIZEBOX back into the descriptor for windows we stripped).
  RED (before the adapter existed): 11 of 21 `MaximizeBlockTests` failed (strip, release, apply,
  minimize/fullscreen/evict restore, admission verdict, T3 cases), plus
  `TilingModeTests.TurningTilingOff_GivesEveryTiledWindowItsMaximizeBoxBack` and
  `TurningTilingBackOn_TakesTheMaximizeBoxOfEveryTiledWindowAgain` (wiring). Some "restore"
  facts passed vacuously against the no-op stubs by construction. Mutation checks: reverting
  `IsExcludedAsAdmitted` fails `AStrippedWindow_KeepsItsAdmissionVerdict_...`. GREEN afterwards.
- [x] T3: Adapter: maximize fallback (restore + arrange, exempt from `Judge`, fullscreen untouched).
  Route: same delegated writer (trigger: 2+ non-trivial files). In `OnWindowBoundsChanged`, after
  the fullscreen guard: `WS_MAXIMIZE` -> `window.TryRestore()`, traced, then the existing reflow
  puts it in its slot. Exempt from `Judge` only when the restore succeeded (a window the OS will
  not restore stays under the fighter guard). Mutation check: removing the `!undidMaximize`
  exemption fails `RepeatedMaximizing_NeverCountsTowardEviction` (the repeated identical
  maximize landing reads as `MinimumSize` and untiles the window). Fullscreen, tiling-off and
  user-gesture (Aero Snap) cases covered.
- [ ] T4: README behavior + limits. Route: same delegated writer.
- [ ] T5: Hardware check (Notepad, Explorer, Chrome, VS Code; caption button, double-click, Win+Up;
  tiling off restores the button; F11 fullscreen unaffected). Route: inline, PENDING until the
  maintainer allows a supervised run.

## Acceptance criteria

- Tiled window with tiling on: maximize button disabled; maximizing by any route ends in its slot.
- Tiling off, window removed/untiled/fullscreen, or normal exit: original button back.
- Fullscreen windows untouched; admitted windows keep their admission verdict.
- `dotnet test CosmicWin.sln` green.

## Progress

- 2026-09-30: branch `feat/block-maximize-while-tiling` from main f46d0f4.
