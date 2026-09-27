# Wallpaper scene over HTTP

## Objective

Switch the html wallpaper scene (processing | explorer | idle | raphael) live, without restarting
the app, through a new local HTTP route `POST /v1/wallpaper/scene`.

## Problem / why

Today the scene is read once from `settings.conf` (`wallpaper-scene`) at startup; changing it means
editing the file and restarting. The HTTP server already carries `/v1/alerts` and
`/v1/wallpaper/video`; the scene is the missing live control for html mode.

## Scope

- New pure protocol `WallpaperSceneHttpProtocol` (Interop): path `/v1/wallpaper/scene`, body
  `{ "scene": "<name>" }`, closed allow-list, small body cap.
- `LocalHttpCommandServer`: third optional route, same gates (loopback, no Origin, Host, POST,
  bearer token, JSON content type, body cap). Route absent when its handler is null -> 404.
- `WebViewAlertLayerController.SwitchScene(WallpaperScene)`: re-navigate on the UI thread; a later
  recreate (host change) navigates to the CURRENT scene, not the construction-time one.
- `AppComposition`: scene handler dispatched via `onOwningThread` (STA), never the video MTA thread;
  answers "not available" (503) outside html mode or without an alert layer; persists the scene to
  `settings.conf` like the video route persists its path.
- New settings key `wallpaper-scene-http = on|off` (default off), independent of `alert-http` and
  `video-wallpaper-http` (same "a route whose switch is off answers 404" decision as V4).
- README section "Wallpaper scene over HTTP".

## Constraints

- Scene text never reaches a URL raw: only the four enum members, mapped by `SceneFolderName`.
- Error replies never echo the body.
- No push / no remote work; local commits only.

## Decisions (defaulted by the parent, open to the maintainer)

- Own enable key `wallpaper-scene-http` rather than reusing `video-wallpaper-http`.
- The switch persists to `settings.conf` (mirrors the video route).
- Same scene again -> 202, no re-navigate.

## TDD

Mode: strict (on). Source: session orchestrator config ("Strict TDD Mode: enabled").
Runner: `dotnet test CosmicWin.sln --filter "Category!=RequiresDesktop"`.

## Delivery

Strategy: ask-on-risk. Forecast ~500-700 authored lines (tests included) -> over the ~400 heuristic;
split into work-unit commits per task below.

## Tasks

- [x] S1 - `WallpaperSceneHttpProtocol` + tests (pure validation). Route: delegated writer.
- [x] S2 - `LocalHttpCommandServer` scene route + wire-level tests. Route: delegated writer.
- [x] S3 - `WebViewAlertLayerController.SwitchScene` + current-scene on recreate + tests.
      Route: delegated writer.
- [x] S4 - Settings key `wallpaper-scene-http`, AppComposition handler (UI thread, html guard,
      persist), WireProduction wiring + wiring tests. Route: delegated writer.
- [ ] S5 - README section + hardware check (switch all four scenes live over HTTP).

Route evidence: understanding needed 6+ files (mapping trigger fired, delegated mapper done);
implementation touches 2+ non-trivial files per task (writer trigger).

## Acceptance criteria

- `POST /v1/wallpaper/scene {"scene":"idle"}` with the token, in html mode -> 202 and the wallpaper
  shows the idle scene without restart; the choice survives a restart.
- Unknown scene -> 400; video mode -> 503; key off -> 404; all shared gates behave as on the other
  routes.
- Full test suite green (non-desktop).

## Progress

- 2026-09-27: branch `feat/wallpaper-scene-http` from main 87fadd5; mapping done. Engram mirror
  `odd/wallpaper-scene-http-endpoint/tasks`: PENDING (mem_save refused: multiple active sessions).
- 2026-09-27: S1 done. `WallpaperSceneHttpProtocol` (32 tests, all green) validates the scene body
  against the closed allow-list, case-insensitively, with no filesystem I/O and a 256-byte cap.

- 2026-09-27: S2 done. `LocalHttpCommandServer` gained a trailing optional
  `handleWallpaperSceneSwitch` delegate and its own route, gated the same way the video route is
  (null delegate -> 404 like an unknown path). 10 new scene-route facts added; all 76
  CosmicWin.Interop.Tests green (happy path, bad scene, 503, 500-with-loop-continues, token/method
  gates, disabled-route-is-404, and three-routes-on-one-server).

- 2026-09-27: S3 done. `WebViewAlertLayerController._htmlWallpaperScene` became the mutable
  `_currentScene`, `CreateAsync`'s Navigate now reads it through a new pure `SceneUrl(scene, fps)`
  helper, and `SwitchScene(WallpaperScene)` re-navigates live (no-op on the same scene, records only
  when no controller exists yet). Deviation from the map: `SwitchScene` returns `bool` (accepted,
  per the map's own "your call"), and its one existing source-text assertion
  (`HtmlWallpaperMode_MapsAndNavigatesToTheConfiguredScenePageUnderItsOwnDomain`) was updated because
  the URL construction moved into `SceneUrl`, as the task instructions allowed. All 23
  WebViewAlertLayerControllerTests green; full non-desktop suite green (1171 App + 425 Interop + 198
  Layout + 13 Alert = 1807 passed, 3 skipped, 0 failed).

- 2026-09-27: S4 done. `Settings.WallpaperSceneHttpEnabled` (key `wallpaper-scene-http`, default
  off, independent of `alert-http`/`video-wallpaper-http`). `AppComposition.Wire` gained
  `wallpaperSceneHttpEnabled`/`switchHtmlWallpaperScene`/`persistWallpaperScene`, a
  `HandleWallpaperSceneHttpSwitch` local function (503 outside html mode or with no switch delegate,
  dispatches on `onOwningThread` -- never the video MTA thread -- and persists only once the switch
  itself reports success), and the `createLocalHttpCommandServer` factory seam grew a trailing
  `Func<string,bool>?` scene delegate. `WireProduction` wires `settings.WallpaperSceneHttpEnabled`,
  `alertLayer?.SwitchScene`, and a persist closure mirroring `persistVideoWallpaperPath`. All fake
  factory lambdas across `CosmicWin.App.Tests` updated to the new 6-arg shape. Full non-desktop
  suite green: 198 Layout + 13 Alert + 425 Interop + 1193 App = 1829 passed, 3 skipped, 0 failed;
  `dotnet build CosmicWin.sln` succeeds with 0 errors.

- 2026-09-27: Review of S1-S4 (base 87fadd5, commits 8df363d..d007997; assess medium,
  slice_budget_reached, 1366 lines): consent granted, 1 lens (reliability), APPROVED and
  acknowledged (review-dd5766fd4dc20e41, authority burned). Parent spot check: 32
  WallpaperSceneHttpProtocolTests re-run, passed. Advisory findings (follow-up, non-blocking):
  - WARNING R3-owning-thread-work-unguarded: the posted UI work (SwitchScene + persist) has no
    exception guard and runs after the 202 reply; a throw is unreported / may hit the dispatcher.
  - WARNING R3-persist-shared-stored-capture: scene persist (UI thread) and video persist (MTA
    thread) both read-modify-write the captured `stored` without synchronization.
  - SUGGESTION R3-live-renavigate-unproved: a late NavigationCompleted/ready from the old page after
    the reset is untested (S5 hardware covers it).
  - SUGGESTION R3-orphaned-doc-summary: WebViewAlertLayerControllerTests has a summary block moved
    off VisibilityDecisions_GoThroughTheSharedPolicyClass onto the SceneUrl theory.

## Next step

S5 (README + hardware check) -- parent's job, not this writer's.
