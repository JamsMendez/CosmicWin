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
- [x] S6 - Review follow-ups (accepted by the maintainer 2026-09-27): guard the posted scene work
      (R3-owning-thread-work-unguarded: catch, report via diagnostics, never crash the dispatcher),
      serialize the settings read-modify-write shared by scene and video persist
      (R3-persist-shared-stored-capture), and move the orphaned summary back
      (R3-orphaned-doc-summary). Route: delegated writer.
- [x] S7 - Guard `/v1/wallpaper/video` in html mode: answer 503 instead of starting a player
      (closes html-wallpaper-demo WARNING R3-html-mode-video-switch-not-guarded). Tray pick
      INCLUDED (maintainer 2026-09-27: "Tambien html"): in html mode the tray video pick does
      nothing and records a trace line. Route: delegated writer.
- [x] S5 - README section + hardware check (switch all four scenes live over HTTP; video route
      answers 503 in html mode).

Route evidence: understanding needed 6+ files (mapping trigger fired, delegated mapper done);
implementation touches 2+ non-trivial files per task (writer trigger).
- [x] S8 - Html wallpaper graduates from demo to feature (maintainer 2026-09-27: "Ya no es demo va
      quedar como feat"): default `wallpaper-mode = html` and `wallpaper-scene-http = on`; drop the
      DEMO ONLY labels (code, settings.conf template, README). Accepted consequence: a fresh
      install opens the loopback HTTP port for the scene route. Branch feat/html-wallpaper-default.
      Route: delegated writer.

- [x] S9 - First run writes settings.conf with the defaults (maintainer 2026-09-27: "en la
      instalacion cree un settings.conf con los valores por defecto"; CosmicWin has no installer,
      so first start is the install moment): when the file is missing, write `Settings.Default`
      serialized (with its comments); an existing file is never touched. Also default
      `alert-http = on` (maintainer: "alertas tambien debe estar prendidas"); `alerts-enabled`
      is already on; `video-wallpaper-http` stays off. Route: delegated writer.
- [ ] S10 - Follow-ups (maintainer 2026-09-27: "Dale a esos pendientes chicos"), on branch
      fix/settings-and-trace-followups: (a) `http-server start` trace line reports the scene route;
      (b) a test proves WireProduction reads settings through LoadOrCreate
      (R3-production-loadorcreate-wiring-untested); (c) a failed settings write (first run or any
      Save) leaves a trace line (R3-first-run-write-failure-silent); (d) pin SynchronizedSettingsStore
      save-failure semantics with a test (R3-settings-store-save-failure-semantics-unproved).
      Route: delegated writer.
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

- 2026-09-27: S6 done. `HandleWallpaperSceneHttpSwitch`'s posted `onOwningThread` work now guards
  `switchHtmlWallpaperScene` and `persistWallpaperScene` in SEPARATE try/catch blocks: a throwing
  switch never persists and never escapes onto the dispatcher, a throwing persist never escapes
  either, and each is reported through `desktopTrace` by exception type name only (never the
  message), mirroring `SwitchVideoWallpaper`'s own `import-failed` line. `WireProduction`'s bare
  mutable `stored` local -- read-modify-written by five unsynchronized closures (focus border,
  border colour, tiling, video path, scene) from two different threads (UI STA, video MTA) -- is
  replaced by the new internal `SynchronizedSettingsStore`, which wraps every read-modify-write-and-
  save in one lock; all five `persistXyz` closures now call `settingsStore.Update(...)`. The orphaned
  `/// <summary>` block above `WebViewAlertLayerControllerTests`'s `SceneUrl` theory (belonging to
  `VisibilityDecisions_GoThroughTheSharedPolicyClass`) moved back above its own test. TDD: RED
  observed for both guard tests (real `Assert.Null()` failures showing the uncaught
  `InvalidOperationException`/`IOException` from the posted work) and for
  `SynchronizedSettingsStoreTests` (real `Assert.False()` failure -- a naive unlocked stub lost one
  of two concurrent field updates); all three GREEN after the fix, the concurrency test re-run 5x
  clean. Full non-desktop suite green: 198 Layout + 13 Alert + 425 Interop (422 passed, 3 skipped) +
  1196 App = 1832 passed, 3 skipped, 0 failed; `dotnet build CosmicWin.sln` 0 errors. Committed in
  the same commit as this Progress entry.

- 2026-09-27: S6 assess (base d007997, committed-only): medium, review_due=false, under_budget
  (310 lines) -- pending in the slice, reviewed together with S7. Parent spot check: 8 S6 tests
  (SynchronizedSettingsStore + HttpSceneSwitch) re-run, passed.

- 2026-09-27: S7 done. `SwitchVideoWallpaper` gained ONE guard at its top -- `wallpaperMode ==
  WallpaperMode.Html` -- checked before the collaborator-null check, before
  `onVideoWallpaperThread` ever posts anything: no import, no persist, no player touch, nothing
  queued, and a `desktopTrace?.Record("video-wallpaper phase={phase} skipped reason=html-mode")`
  line using the CALLER's own phase. This single shared guard covers BOTH `SwitchVideoWallpaper`
  callers -- `HandleVideoWallpaperHttpSwitch` (traces `phase=http`) and the tray's own
  `setVideoWallpaperPath` closure, which calls `SwitchVideoWallpaper(path)` with its default
  `phase="pick"` (traces `phase=pick`, not the map's guessed `phase=tray` -- the tray call site has
  never passed a `phase` argument) -- since those are the method's only two callers in the whole
  file; no per-caller duplication was needed. Video-mode behaviour is byte-for-byte unchanged (the
  guard's condition is always false there). Fixed the `htmlWallpaperActive`/`videoWallpaperActive`
  comment (html-wallpaper-demo review, "a comment claims the modes never mix"): the claim is now
  true in practice, but it was only ASPIRATIONAL before this task -- `wallpaperMode` being chosen
  once at Wire time said nothing about `SwitchVideoWallpaper` itself, which had no mode check of
  its own until this task added one; the comment now says so. Also added a remark to
  `HandleVideoWallpaperHttpSwitch` cross-referencing the shared guard. TDD: RED observed for both
  new tests (`HttpSwitch_InHtmlMode_ReturnsFalseWithoutImportingOrTouchingThePlayer`:
  `Assert.False()` got `True`; `TrayPick_InHtmlMode_DoesNothingAndRecordsATraceLine`:
  `Assert.Empty(queued)` found one queued work item) against the pre-fix code, both GREEN after the
  guard. Full non-desktop suite green: 198 Layout + 13 Alert + 425 Interop (422 passed, 3 skipped) +
  1198 App = 1834 passed, 3 skipped, 0 failed; `dotnet build CosmicWin.sln` 0 errors. Committed in
  the same commit as this Progress entry.

- 2026-09-27: Review of S6+S7 (base d007997 -> ff06f8b; assess medium, slice_budget_reached, 461
  lines): consent granted, 1 lens (reliability), APPROVED and acknowledged
  (review-95cb465f03fc6d13, authority burned). Parent spot check: 5 InHtmlMode tests re-run,
  passed. Advisory findings:
  - SUGGESTION R3-settings-store-test-asserts-unused-snapshot: FIXED by the parent in the next
    commit -- the concurrency test now asserts the last SAVED snapshot. Proof: mutating Update to
    save the pre-change snapshot made it fail (Assert.False() Failure); restored -> passes.
  - SUGGESTION R3-settings-store-save-failure-semantics-unproved: open follow-up. Update sets
    _current before save; a throwing save leaves memory ahead of disk until the next successful
    Update writes it. Accepted as eventually convergent; not pinned by a test.

- 2026-09-27: S5 done. README section "Wallpaper scene over HTTP" + html-mode note on the video
  route (ee7f93f). Hardware check, driven by the parent (elevated shell, run.ps1 copy, port 47811
  free), measured from desktop-trace.log and HTTP replies:
  - html mode: processing, explorer, raphael, idle -> 202 ok each, each followed by
    `alert-layer navigation completed success=True` + `page ready` (138-295 ms);
    settings.conf `wallpaper-scene` followed every switch.
  - same scene (`IDLE`, upper case) -> 202, NO navigation line (no re-navigate).
  - unknown scene -> 400; extra field -> 400 `unknown field`; 300-byte body -> 413.
  - video mode restart -> scene route 503 `wallpaper scene switching is not available`.
  - html mode: an external client already POSTing /v1/wallpaper/video once a second now gets 503
    with `video-wallpaper phase=http skipped reason=html-mode` (S7 confirmed live).
  - NOT verified: the scene visibly changing on screen (desktop covered by windows in the
    screenshots) -- left for the maintainer's eyes.
  - Follow-up: the `http-server start requested ... alerts-route=True video-route=True` trace line
    does not report the scene route.
  Maintainer settings restored from backup, plus `wallpaper-scene-http = on`; app left running in
  html mode (idle scene).

- 2026-09-27: S8 done, on `feat/html-wallpaper-default` (off main 8d3bcea). `Settings`'s record
  defaults flipped: `WallpaperMode = WallpaperMode.Html`, `WallpaperSceneHttpEnabled = true` (both
  `alert-http`/`video-wallpaper-http` and the scene/fps defaults themselves untouched). `Parse`'s
  fallback-to-default behaviour needed no code change -- it already reads its initial locals from
  `Settings.Default`, so an empty/invalid `wallpaper-mode` or `wallpaper-scene-http` line now falls
  back to Html/on for free. `Serialize`'s template comment for `wallpaper-mode` now reads "`html`
  (default) shows an animated HTML scene ... `video` plays the configured video instead"; the
  `wallpaper-scene-http` comment says "on (default)". Removed every `DEMO ONLY`/`DEMO-ONLY`/"not a
  supported feature" label from `Settings.cs` (11 sites: both enum docs, four param docs, three
  private-const summaries, three `Serialize`/`TryRead*` comments) and `AppComposition.cs` (4 sites:
  the `wallpaperMode` parameter doc, `AttachHtmlWallpaper`'s doc, and the two
  `WebViewAlertLayerController` construction-site comments), rewriting each as a description of a
  supported feature while keeping the D3/D6d/S4/S8 task-ID provenance. `AppComposition.Wire`'s own
  `wallpaperMode = WallpaperMode.Video` parameter default was deliberately LEFT as Video and
  annotated why: it is a test seam (every video-playback wiring test in `AppCompositionTests`/
  `VideoWallpaperPlaybackWiringTests` calls `Wire()` without naming `wallpaperMode`, relying on this
  default); production never reads it -- `WireProduction` always passes
  `wallpaperMode: settings.WallpaperMode`, whose own default is now Html.
  TDD: RED observed first for 11 failing assertions across
  `WallpaperModeDefaultsToHtml`/`AnUnreadableWallpaperMode_KeepsTheDefaultRatherThanGuessing`(x3)/
  `WallpaperSceneHttpIsOn_UnlessTheFileSaysOtherwise`/
  `AnUnreadableWallpaperSceneHttpValue_KeepsTheDefaultRatherThanGuessing`(x3)/
  `Serialize_IncludesTheWallpaperModeAndDescribesEachValue`/
  `Serialize_IncludesTheWallpaperSceneAndDescribesEachValue`/
  `Serialize_IncludesTheWallpaperFpsAndDescribesEachValue` (real `Assert.Equal`/`Assert.True`/
  `Assert.Contains` failures against the pre-change code, e.g. `WallpaperModeDefaultsToHtml`: Expected
  Html, Actual Video), all GREEN after the defaults/label changes. README's "Wallpaper scene over
  HTTP" section rewritten: states `wallpaper-mode = html` is the default, and the scene route is **on
  by default** (fresh install opens the loopback port), with `wallpaper-scene-http = off` shown as the
  opt-out; the "Video wallpaper over HTTP" 503 row/paragraph now note html mode is the default.
  `WireProductionHtmlWallpaperSettingsWiringTests.cs`'s doc comment dropped "demo-only setting"
  (provenance-only prose, no assertion change). Full non-desktop suite green: 198 Layout + 13 Alert +
  425 Interop (422 passed, 3 skipped) + 1198 App = 1834 passed, 3 skipped, 0 failed (same totals as
  S7 -- no tests added or removed, only reassigned expectations); `dotnet build CosmicWin.sln` 0
  errors. Remaining `rg -i demo` hits outside `Web/`/`odd/`: all provenance-only task-ID references
  (`D3 (html-wallpaper-demo)`, `D6d (html-wallpaper-demo)`, etc.) in `AppComposition.cs`,
  `WebViewAlertLayerController.cs`, `WebViewAlertLayerVisibility.cs`, and their test files -- none
  claim the feature is unsupported, so left as historical identifiers per the task's own instruction;
  the false-positive "demonstrate(d)" hits in layout files are unrelated. Committed in the same commit
  as this Progress entry.

- 2026-09-27: S9 done, on `feat/html-wallpaper-default` (off S8's d581f4f). `Settings`'s
  `AlertHttpEnabled` record default flipped `false` -> `true` (maintainer: "alertas tambien debe
  estar prendidas"); its XML doc and the `Serialize` template comment for `alert-http` rewritten to
  justify ON-by-default the same way `WallpaperSceneHttpEnabled`'s already did (loopback-only, bearer
  token, accepted consequence for a fresh install), rather than the old off-by-default justification.
  `alerts-enabled` and `wallpaper-scene-http` were already on (S8); `video-wallpaper-http` untouched,
  still off. `SettingsFile` gained `LoadOrCreate(path)` / `LoadOrCreate()`: when the file does not
  exist it calls the EXISTING `Save` (reused, not duplicated -- `Save` already swallows
  `IOException`/`UnauthorizedAccessException` and already creates the directory) with
  `Settings.Default`, then returns `Settings.Default`; when the file exists it is exactly `Load` --
  read, never rewritten, not even to normalise formatting. `Load` itself is untouched and stays
  side-effect-free for its other two callers (`loadGap`'s Reload closure, all `SettingsFile.Load`
  test call sites). `AppComposition.WireProduction`'s one `var settings = SettingsFile.Load();` (the
  sole production read, ahead of `Wire`) became `SettingsFile.LoadOrCreate()` -- the one seam that
  needed the write; `loadGap: () => SettingsFile.Load().Gap` deliberately kept as plain `Load` (a
  Reload must not create or rewrite the file either). A write failure degrades to
  `Settings.Default` exactly like an unreadable existing file already does, via `Save`'s own
  swallowed catch -- no new diagnostic added, since `Save` itself records nothing on failure and S9
  was told to reuse, not duplicate, whatever pattern it already had.
  TDD: RED observed for 7 assertions before the fix -- `SettingsTests.AlertHttpIsOn_UnlessTheFileSaysOtherwise`
  (`Assert.True()` got `False` against the old `false` default) and 3
  `AnUnreadableAlertHttpValue_KeepsTheDefaultRatherThanGuessing` theory cases (same failure), plus 3
  new `SettingsFileTests` facts against a deliberate `LoadOrCreate(path) => Load(path)` stub with no
  write: `MissingFile_LoadOrCreate_WritesTheDefaultsToDisk` (`Assert.True(File.Exists(...))` got
  `False`), `MissingFile_LoadOrCreate_WritesTheFullCommentedTemplate`
  (`DirectoryNotFoundException` reading a file that was never created),
  `MissingFile_LoadOrCreate_TheWrittenDefaultsMatchTheS9Decisions` (`Assert.True(settings.AlertHttpEnabled)`
  got `False`); all 7 GREEN after flipping the default and implementing the real `LoadOrCreate`. Two
  more new facts (`ExistingFile_LoadOrCreate_IsNeverRewritten`,
  `AnUnwritablePath_LoadOrCreate_ReturnsTheDefaultsInsteadOfThrowing`) already passed against the stub
  since it happened to delegate to the untouched `Load`, and still pass unchanged against the real
  implementation. README's HTTP-alerts section rewritten: on by default, loopback-only, bearer
  token, `alert-http = off` shown as the opt-out, plus a line stating CosmicWin writes settings.conf
  itself on first start and never rewrites an existing one. Full non-desktop suite green: 198 Layout
  + 13 Alert + 425 Interop (422 passed, 3 skipped) + 1203 App = 1839 passed, 3 skipped, 0 failed (5
  more than S8's 1834 -- the 5 new `SettingsFileTests` facts, no App tests removed); `dotnet build
  CosmicWin.sln` 0 errors.

- 2026-09-27: Review of S8+S9 (base 8d3bcea, commits d581f4f..050b19d; assess medium,
  slice_budget_reached, 473 lines): consent granted, 1 lens (reliability), APPROVED and
  acknowledged (review-ed64317909a5c82a, authority burned). Parent spot checks: 201 SettingsTests
  and 11 SettingsFileTests re-run, passed. Advisory findings:
  - WARNING R3-upgrade-default-flip-unproved: an EXISTING settings.conf with no `wallpaper-mode`
    / `alert-http` line (older installs, hand-edited files) also flips to html / opens the port on
    upgrade, silently. DECIDED by the maintainer 2026-09-27: accepted as intended (option 1, same
    defaults for upgrades as for fresh installs); no code change.
  - SUGGESTION R3-production-loadorcreate-wiring-untested: nothing proves WireProduction calls
    LoadOrCreate rather than Load.
  - SUGGESTION R3-first-run-write-failure-silent: a failed first-run write leaves no trace.

- 2026-09-27: Maintainer confirmed on screen: the scenes really change live over HTTP, and alerts
  work in html mode. Closes the "visual change not eyeballed" gap from S5.

## Next step

Maintainer: decide on merging `feat/html-wallpaper-default` into main. Follow-ups: scene-route
trace line, R3-settings-store-save-failure-semantics-unproved.
