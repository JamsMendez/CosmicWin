# Alert layer: a superseded navigation is not a failure

## Problem

In html wallpaper mode, two scene switches in quick succession (`POST /v1/wallpaper/scene` twice,
~50 ms apart) make `WebViewAlertLayerController.SwitchScene` call `CoreWebView2.Navigate` while the
previous navigation is still in flight. WebView2 aborts the older one and raises `NavigationCompleted`
for it with `IsSuccess=false`, `WebErrorStatus=ConnectionAborted`. `OnNavigationCompleted` treats ANY
failure as fatal: `TearDown("navigation-failed")` -> the whole layer is recreated (~1 s), and an
alert being shown at that moment is lost (never re-shown).

Evidence: 58 occurrences in desktop-trace.log since 2026-09-27, each with a tiny elapsed time
(1-74 ms since the last host `Navigate`, i.e. a `SwitchScene` just happened). Reproduced on hardware
2026-10-01 (Release 367feaf, html mode): one switch -> clean `page ready`; two switches 50 ms apart ->
`navigation completed success=False status=ConnectionAborted 1ms` -> `close reason=navigation-failed`
-> `create start` ... `page ready`.

The mini scene window is NOT affected (`WebView2MiniSceneBrowser.OnNavigationCompleted` ignores
failures without tearing down).

## Fix

Correlate completions with the latest navigation: record `NavigationId` from `NavigationStarting`
(latest wins); in `OnNavigationCompleted`, a completion whose `NavigationId` is not the latest is
superseded -> trace it (e.g. `alert-layer navigation superseded id=N`) and return without touching
state. A failure of the LATEST navigation still tears down exactly as today.

## Constraints

- TDD strict. Runner: `dotnet test CosmicWin.App.Tests/CosmicWin.App.Tests.csproj`.
- WebView2 cannot run in tests: put the decision in a small pure seam (e.g. a static helper or tiny
  tracker class) that is unit-tested, and keep the controller wiring thin.
- One work-unit commit, Conventional Commit, no AI attribution.

## Tasks

- [x] T1 -- superseded completions ignored (pure seam + tests + controller wiring + trace line).
  Route: delegated direct (writer; 2+ files).
- [ ] T2 -- hardware: two scene switches 50 ms apart in html mode -> `superseded` line, no
  `navigation-failed`, no recreate; single switch unchanged. Route: parent.

## Out of scope (recorded)

- Re-showing an alert that was showing when the layer IS legitimately recreated (process failure,
  host change). Separate, optional.

## Progress

- 2026-10-01: branch `fix/alert-layer-superseded-navigation` off main 367feaf.
- 2026-10-01 T1 done (commit recorded in git log, subject "fix(alert-layer): ignore completions of superseded navigations"). Seam
  `AlertLayerNavigation.IsSuperseded(completedId, latestStartedId)` (older id => superseded, success
  included; no recorded start => not superseded); controller records `NavigationStarting.NavigationId`
  (reset + unsubscribed in TearDown). Trace: `alert-layer navigation superseded id=<N> status=<WebErrorStatus>`.
  RED: 3 assertion failures (seam stub returning false, trace stub returning "", wiring guard);
  GREEN: 103 focused pass; mutation `<` -> `<=` failed 1 seam test, restored. Full App suite 1409 passed / 6 skipped (baseline 1404 + 5 new); sln build 0 errors, no warnings in touched files.
