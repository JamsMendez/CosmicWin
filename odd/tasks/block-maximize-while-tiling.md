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
- [x] T4: README behavior + limits. Route: same delegated writer (trigger: 2+ non-trivial files).
  Tiling section: one bullet under "What works" plus a "Maximize while tiling" subsection (what is
  blocked, when the button returns, pause behaviour, the three limits); `tiling` settings row
  mentions maximize. Docs only, no RED/GREEN applicable; README rendered by readback.
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
- 2026-09-30: T1 d8b8eba, T2 5fc56f2, T3 e3b7ab3, T4 (README) committed on the branch by one delegated writer. Verification: `dotnet build CosmicWin.sln` 0 errors; Interop.Tests 435 passed/42 skipped; Layout.Tests 198 passed; App.Tests 1344 passed/6 skipped. T5 (hardware check) still PENDING.
- Parent spot check: `dotnet test CosmicWin.Interop.Tests` re-run, 435 passed / 42 skipped / 0 failed.
  New native calls are synchronous cross-process like the existing `SetWindowPosition` (no new
  hang class).
- Review (RDD, medium, 1310 lines, consent granted): lineage review-7a0549b5722a49d2, lens
  review-reliability, APPROVED and acknowledged (authority burned) on ff703b0. Advisory follow-ups:
  R3-style-readback-full-compare (`TrySetMaximizeBox` compares the WHOLE style on read-back; if the
  app or Windows changes another bit at the same moment, a successful strip reads as "refused",
  the handle is never recorded as stripped and its button is never given back -- fix: compare only
  the maximize-box bit), R3-strip-without-restore-on-admit (adapter ~1749-1760), and
  R3-unrestorable-test-weak (MaximizeBlockTests ~456-471).
- Follow-up R3-style-readback-full-compare FIXED (route: inline, 1 production file + 1 test file,
  mechanical): `TrySetMaximizeBox` now checks only the maximize-box bit through the pure
  `Win32NativeWindowSource.MaximizeBoxApplied(readBack, enabled)`. RED: 9/9
  `Win32NativeWindowSourceMaximizeBoxTests` failed against a throwing stub; GREEN 9/9; full
  `dotnet test CosmicWin.Interop.Tests` 444 passed / 42 skipped / 0 failed; `dotnet build
  CosmicWin.sln` 0 errors.
- Second review (whole branch incl. acb0aa7, 1404 lines, consent granted): lineage
  review-6b208b1e7780e5d5, APPROVED and acknowledged (authority burned) on acb0aa7. Advisory
  follow-ups: R3-apply-block-activates-many (turning tiling back on calls `SW_RESTORE` on every
  maximized tiled window, and `SW_RESTORE` activates, so focus can hop across several windows),
  R3-restore-result-dropped (giving the box back ignores a failed write, untraced),
  R3-strip-after-addwindow-untile (adapter ~540: confirm the window is still tiled before
  stripping), R3-unrestorable-test-weak (still open).
- Follow-up R3-apply-block-activates-many FIXED (route: delegated writer, trigger "2+ non-trivial
  files"): `ApplyMaximizeBlock(nint foregroundBefore = 0)`; `AppComposition.ResumeTiling` reads the
  foreground BEFORE the batch and, after a batch that restored at least one window, the adapter
  gives the foreground back through `TryActivate`. Chosen over a non-activating restore because
  `SW_SHOWNOACTIVATE` has no documented maximized case. The single-window fallback keeps activating
  (the window the user just maximized already holds focus). Known limit: a foreground window the
  registry does not know cannot be handed back. RED:
  `MaximizeBlockTests.ApplyingTheBlock_AfterRestoringSeveralWindows_GivesTheForegroundBackToWhoHadIt`
  failed (expected handle 10, actual 20: the foreground ended on the last restored window) against
  a no-op stub of the new parameter; GREEN 26/26 in `MaximizeBlockTests`. Commit: see git log.
- Follow-up R3-restore-result-dropped FIXED (same delegated writer): `RestoreMaximizeBox` now traces
  `maximize box not returned hwnd=... class=... proc=...` when `TrySetMaximizeBox(true)` fails on a
  live window; a success records nothing. RED:
  `MaximizeBlockTests.AFailedGiveBack_IsTraced_WithTheWindowIdentity` failed, the trace held only the
  `guard state`/`added` lines and no such line (the first RED run; a second failure after the
  implementation was my own test typo, `0x0A` for `0xA`, fixed). GREEN 28/28 in `MaximizeBlockTests`
  (the success-path fact `ASuccessfulGiveBack_IsNotTraced` passes by construction against the old
  code too). Commit: see git log.
- Follow-up R3-strip-after-addwindow-untile FIXED (same delegated writer). Investigation: in the
  straight-line path of `AddWindow` the window cannot leave the tree and still pass
  `CanReposition` (floor failure `Untile`s and returns; fullscreen returns; the arrange evicts only
  on `!CanReposition`, which already returns). It CAN be removed by re-entrancy: the arrange runs
  the after-arrange listener, which may remove the window being admitted. RED:
  `MaximizeBlockTests.AWindowThatLeavesTheTreeWhileItIsBeingAdded_IsNeverStripped` failed (a
  `TrySetMaximizeBox(false)` request reached a window the adapter had already forgotten). Fix: the
  strip is skipped unless the window still has a registry leaf. GREEN `MaximizeBlockTests` 29/29.
  Also: README reworded (button is disabled/greyed, not removed) at the maintainer's request.
  Commits: see git log.
- Third review (whole branch incl. the three fixes and README rewording, 1618 lines, consent
  granted): lineage review-aff2dac8adb0b542, APPROVED and acknowledged (authority burned) on
  72c7e83. Advisory follow-ups: R3-native-style-calls-unbounded (the style write and restore are
  synchronous cross-process calls with no timeout; same class as the existing `SetWindowPosition`,
  a hung target window can stall the caller), R3-restore-contract-mismatch
  (`INativeWindowSource.TryRestoreFromMaximized` doc says it reports "whether the window was
  asked", the implementation reports whether it left the maximized state; fix the doc).
  R3-unrestorable-test-weak still open.
- Follow-up R3-native-style-calls-unbounded FIXED (route: delegated writer, trigger "2+ non-trivial
  files"): `RunBounded` generalized to `RunBounded<T>(attempt, budget, whenFailed, whenTimedOut)`
  (the activation overload delegates to it); new `RunStyleCall` bounds `TrySetMaximizeBox` and
  `TryRestoreFromMaximized` under `StyleCallTimeout` (= the 250 ms activation timeout). A timeout
  or a throw answers false, so the adapter's refused/trace paths apply unchanged. Late completion:
  an abandoned write cannot be cancelled and may land later, so a strip answered "refused" could
  leave a disabled button unrecorded. Chosen: the adapter still gives the box back for a refused
  strip on release (quietly; asking for a bit the window still has is a no-op, so genuine UIPI
  refusals cost one style read and are not traced). The unbounded `SetWindowPosition` is the same
  class and stays out of scope. RED: `BoundedStyleCallTests` 4/4 failed (NotImplementedException
  stub) and `MaximizeBlockTests.AStripThatTimedOutButLandedLater_IsStillGivenBackWhenTheBlockIsReleased`
  failed (button not returned) plus the updated elevated-window fact (`[false, true]` requests).
  GREEN: Interop.Tests 448 passed / 42 skipped; MaximizeBlockTests 30/30.
