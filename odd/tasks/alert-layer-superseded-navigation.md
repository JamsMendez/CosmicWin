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
- [x] T2 -- hardware: two scene switches 50 ms apart in html mode -> `superseded` line, no
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
- 2026-10-01 T1 redo: 0aef7b3 failed on hardware (order of events): WebView2 completes the aborted
  navigation BEFORE raising NavigationStarting for the new one, so the id comparison never fired.
  Now exact-id bookkeeping: the host calls `AlertLayerNavigation.BeforeHostNavigate()` before each
  Navigate (SwitchScene + both CreateAsync branches), marking the in-flight id abandoned; a completion
  of an abandoned id is superseded and consumed; set cleared in TearDown; no `<` comparison. Trace lines
  now: `alert-layer navigation starting id=N`, `navigation completed id=N success=.. status=.. Nms`,
  `navigation superseded id=N status=..`. RED: 8 assertion failures (incl. the abort-before-starting
  model); GREEN 50 focused; mutations (never match abandoned; never abandon) each failed 5 seam tests.
  Full App suite 1415 passed / 6 skipped; build 0 errors, no new warnings. T2 hardware still pending.
- 2026-10-01: T2 hardware, Release publish of 17743e3, html mode (settings restored to html-mini after):
  single switch -> `starting id=3`, `completed id=3 success=True`, `page ready`;
  two switches 50 ms apart -> `starting id=4`, `starting id=5`, `superseded id=4`, `completed id=5
  success=True`, `page ready` -- no navigation-failed, no recreate;
  three switches 30 ms apart -> `starting id=6`, `superseded id=6 status=ConnectionAborted`,
  `starting id=7`, `superseded id=7 status=ConnectionAborted`, `starting id=8`, `completed id=8
  success=True` -- confirms the abort completion arrives BEFORE the new NavigationStarting (why
  0aef7b3 could not work). Before the fix the same repro traced navigation-failed + recreate.
- Found during T2 (pre-existing, NOT fixed here): a scene switch while an alert is showing reloads
  the page and the alert is NOT re-shown (`show` at :04.48, switch :06.51, `page ready` :06.88, no
  pending-show line, `hide` at :12.68) -- the alert vanishes for the rest of its duration.
  Also seen: an external HTTP client was sending alerts/scenes during the run (`alert ignored: one is
  already showing` for a probe alert), which explains the unexplained switches in the trace.

- 2026-10-01: review-ca57113fb799cd52 (medium, reliability, 367feaf..1e47795) APPROVED and acknowledged.
  Its WARNING R3-navigate-before-starting-race fixed right after: the tracker also remembers the
  latest NavigationStarting, and a completion of any OTHER id (exact inequality) is superseded, so two
  host Navigate calls before the first start is delivered no longer leave the older abort fatal.
  RED: new seam test `TwoHostNavigatesBeforeEitherStarted_...` failed (Assert.True), then GREEN 51/51
  focused; full App 1416 passed / 6 skipped; hardware repro repeated: same clean superseded traces.
  Residual (advisory): if the first navigation's abort completes BEFORE either start is delivered,
  nothing identifies it as stale; not observed on hardware.
- 2026-10-01: review-fb68dcc27e3d96bc (medium, reliability, 1e47795..76d9f87) APPROVED and
  acknowledged. SUGGESTION R3-reset-latest-started-untested closed: new seam test
  `Clear_ForgetsTheLatestStart_...` (mutation removing `_latestStarted = null` in Clear fails it).
  WARNING R3-latest-started-broadens-staleness recorded as an accepted advisory: any completion whose
  id differs from the latest NavigationStarting now counts as superseded, which would misfire only if
  a navigation the host did not start overtook the host's own (the scene pages never navigate
  themselves -- verified by searching shared/ and every scene for location/href/open/history) or if
  a start were ever missed. Revisit if a `superseded` line is ever followed by a layer that never
  reports `page ready`.

## Next step

Fix complete and hardware-checked. Open, separate: a scene switch while an alert is showing drops
the alert for the rest of its duration (see T2 notes).
- 2026-10-01: review-4392cc4859d40afa (medium, reliability, whole range 367feaf..2e956b6, asked by the
  stop hook) APPROVED and acknowledged. Two SUGGESTIONs open: R3-class-doc-contradicts-latest-started-rule
  (AlertLayerNavigation summary still describes only the abandoned-id rule) and R3-wiring-order-unasserted
  (the source guard counts BeforeHostNavigate calls but not that each precedes a Navigate call).
- 2026-10-01: both SUGGESTIONs of review-4392cc4859d40afa closed. R3-wiring-order-unasserted: the
  source guard now requires every `CoreWebView2.Navigate(` line to be immediately preceded by
  `_navigation.BeforeHostNavigate();` (mutation moving the call after Navigate in SwitchScene fails it;
  the old count-only check passed that mutation). R3-class-doc-contradicts-latest-started-rule: the
  AlertLayerNavigation summary now lists both superseded rules. Alert-layer tests 111/111, build 0 errors.
