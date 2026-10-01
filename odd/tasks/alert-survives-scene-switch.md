# An alert survives a scene switch

## Problem

In html wallpaper mode, `WebViewAlertLayerController.SwitchScene` navigates the page to the new scene
while an alert may be showing. The new page loads without the alert and nothing re-posts it, so the
alert vanishes for the rest of its duration. Hardware 2026-10-01 (Release 17743e3): `show` at
19:15:04.48 (8 s), scene switch at :06.51, `page ready` at :06.88, no `pending show applied`, queue
`hide` at :12.68.

## Fix

`AlertLayerPreloadState` already handles the same case for a torn-down controller: `ControllerLost()`
requeues a showing alert as pending for its REMAINING time and `TryMarkReady` re-applies it via
`ApplyPendingShowIfDue()`. A scene switch is the same situation with the controller kept: before the
host navigates in `SwitchScene`, mark the page as reloading (not ready; showing alert -> pending), so
the new page shows it again on ready, or drops it if its deadline passed meanwhile.

## Also in this branch

- Review review-8813765b8c444e36 SUGGESTION R3-superseded-early-return-order-unasserted: the source
  guard must also prove the superseded early return precedes the failure branch in
  OnNavigationCompleted.

## Constraints

TDD strict; runner `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`. Work-unit commits,
no AI attribution.

## Tasks

- [x] T1 -- state: `PageReloading()` (pure, unit-tested) + `SwitchScene` calls it before navigating
  (structural guard). Route: inline (small, understood).
- [x] T2 -- guard: superseded early return precedes the IsSuccess failure branch. Route: inline.
- [x] T3 -- hardware: alert showing, scene switch -> `pending show applied` after `page ready`,
  alert visible until the queue's `hide`. Route: parent.

## Progress

- 2026-10-01: branch `fix/alert-survives-scene-switch` off main 9ac67d7 (pushed).
- 2026-10-01: T1 inline. `AlertLayerPreloadState.PageReloading()` (= ControllerLost semantics: not
  ready, a showing alert requeued for its remaining time, backoff untouched); `SwitchScene` calls it
  before navigating. RED: with an empty stub `PageReloadingWhileShowingRequeues...` failed; GREEN 17/17.
  Wiring guard (SwitchScene calls PageReloading before Navigate) RED before the wiring, then GREEN.
- 2026-10-01: T2 inline. Guard: in OnNavigationCompleted, `CompletedIsSuperseded(` precedes
  `if (!args.IsSuccess)`; mutation moving the superseded block after the failure branch fails it.
  Full App suite 1420 passed / 6 skipped.
- 2026-10-01: T3 hardware, Release publish of this tree, html mode (restored to html-mini after):
  `show` failed 8000 at :19.63, scene switch -> `starting id=3` :21.66, `completed id=3 success=True`
  + `page ready` :22.05, `pending show applied ... remaining=5579` :22.06, `done` :27.68, `hide`
  :27.78 -- the alert is back on the new page for its remaining time.

