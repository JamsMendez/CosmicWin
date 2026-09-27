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
- [ ] S3 - `WebViewAlertLayerController.SwitchScene` + current-scene on recreate + tests.
      Route: delegated writer.
- [ ] S4 - Settings key `wallpaper-scene-http`, AppComposition handler (UI thread, html guard,
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

## Next step

S3.
