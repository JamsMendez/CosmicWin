# Strip CosmicWin to tiling + video wallpaper

Branch: `feat/strip-to-tiling-video` (off main 9f49973). Backup of the full feature set: branch `bk/cosmicwin-full` (= main 9f49973).
Engram mirror: `odd/strip-to-tiling-video/tasks`.

## Objective
CosmicWin keeps ONLY the tiling manager plus the video wallpaper imported from the tray. Everything else is removed.

## Why
Separate products: CielWin (../CielWin) carries scenes, alerts, HTTP and mini; CosmicWin stays lean. The original Phase 0 (port video/tint/pipe into CielWin) was cancelled by the maintainer on 2026-10-02: CielWin will not use the video endpoint. Anything CosmicWin-only can be recovered from `bk/cosmicwin-full`.

## Keep
- Tiling (TreeManager, arrange, gap setting, fullscreen-window handling in tiling).
- Video wallpaper: tray MP4 pick, hard-link import (VideoWallpaperImport without the snapshot), MediaFoundation player, Win32VideoWallpaperHost with the DComp swapchain, TryAttach, TaskbarCreated re-attach, slideshow re-raise, keep-alive on the reconcile tick, MtaActionThread.
- FakeVideoWallpaperHost / FakeVideoWallpaperPlayer (App.Tests references Interop.Tests).

## Remove
HTTP server + token + all routes, named pipe + CosmicWinAlert CLI, WebView2 alert layer, see-through tint, video shake, PrimaryMonitorFullscreenDetector (alert/scene only), ICompositionOverlaySurface, html-mini + Alt+M, HTML scenes + html mode + WebView2 package.

## Decisions
- After the collapse there is no wallpaper mode: the video plays whenever `video-wallpaper-path` is set. Old `wallpaper-mode`, `wallpaper-scene`, `wallpaper-fps`, `mini-*`, `http-*`, `alert-http*` and `alerts` lines are parsed and ignored like other dropped keys (no settings.conf error).
- Order removes inputs/modes first, then the renderer (consumers before providers), so each unit builds green.
- Delivery: one local feature branch, one work-unit commit per task; the maintainer merges and pushes. Deletion-heavy, so the ~400 line heuristic does not drive slicing.

## Tasks
Route for every code task: delegated direct (one writer; 2+ non-trivial files).

- [x] T1 (U1) Remove html-mini + Alt+M (~2.6k lines). Route: delegated writer. 11 files deleted, 66+/2840-. Old `wallpaper-mode = html-mini|mini`, `mini-position`, `mini-corner` parse to defaults (new SettingsTests). AlertLayerMessages wire tests moved to Alerts/AlertLayerMessagesTests.cs. Checks: build 0 errors; tests Layout 198, Alert CLI 13, Interop 553/42 skipped, App 1342/6 skipped, 0 failed; rg C# sweep empty (JS mini variant goes in T2). Commit 7bc0c7f. RDD: medium, granted; review-decf03f6e1af3783 approved + acknowledged (1 lens, reliability). Advisory SUGGESTIONs, both obsoleted by later units: R3-001 HandleWallpaperSceneHttpSwitch ternary lost its routing tests (route removed in T3); R3-002 html-mini silently falls back to html with no trace (wallpaper-mode is removed entirely in T2).
- [x] T2 (U2) Remove html mode + HTML scenes; settings collapse (no WallpaperMode/scene/fps). Route: delegated writer. 71 files deleted (Wallpaper/Web, scene tests, WebViewAlertLayerVisibility, WallpaperSceneHttpProtocol). Video plays whenever video-wallpaper-path is set; retired wallpaper-mode/scene/fps/mini-* lines parse to defaults (RetiredWallpaperLines_* tests). Alert controller serves only the alert page. Checks: build 0/0; Layout 198, CLI 13, Interop 511/42 skip, App 1248/6 skip, 0 failed; rg sweep empty. Review of the single 20k-line commit refused (lens_context_budget_exceeded), so T2 was split by maintainer choice: T2a = C#/tests (scene assets still on disk, dead; App 1253/6 skip incl. 5 scene node tests, 0 failed), T2b = delete the dead Wallpaper/Web assets, scene tests and their csproj Content items (67 files, 17037-; Layout 198, CLI 13, Interop 511/42, App 1248/6, 0 failed). RDD: T2a review refused again (lens_context_budget_exceeded); maintainer chose: try review per unit, continue without it when the budget refuses, proof = build + suites + rg + hardware T7.
- [x] T3 (U3) Remove HTTP server, token, routes, same-video snapshot no-op. Route: delegated writer. 11 files deleted (LocalHttpCommandServer, AlertHttpProtocol, VideoWallpaperHttpProtocol, VideoWallpaperFileProbes, AlertHttpTokenFile + tests). SwitchVideoWallpaper traces phase=pick, always reloads. Retired http-*/alert-http* lines parse to defaults (RetiredHttpLines_*). Pipe wiring coverage moved to Alerts/PipeAlertCompositionWiringTests.cs (4 tests). Checks: build 0 errors; Layout 198, CLI 13, Interop 366/42 skip, App 1174/6 skip, 0 failed; rg sweep empty. Commit ad8f248. RDD: granted, refused with lens_context_budget_exceeded -> no review (policy). Whole-branch candidate review-f90b35ac52206874 (granted) was refused as invalid_request because T3 was mid-edit.
- [x] T4 (U4) Remove named pipe + CosmicWinAlert(+Tests) projects. Route: delegated writer. Projects removed from sln; AlertPipeName/Protocol, IAlertCommandServer, NamedPipeAlertCommandServer + tests deleted; 3 App wiring test files that drove alerts through the fake pipe deleted (alert display goes in T5); 11 pipe-only NativeMethods entries removed. No production alert input remains. Checks: build 0 errors; Layout 198, Interop 335/42 skip, App 1150/6 skip, 0 failed; rg sweep empty. Commit f2f6292. RDD: granted; review-c9b6cb6d690149a4 approved + acknowledged (reliability). Advisory, both closed by T5 deleting the code: R3-retained-behavior-tests-deleted (WARNING: preload/keep-alive alert wiring tests deleted while that code stays until T5); R3-inert-alert-path-still-live (alert queue/preload/overlay tick still run with no input). Whole-branch candidate review-7c16d00aba12f550 granted, refused lens_context_budget_exceeded.
- [x] T5 (U5) Remove alert layer, tint, shake, fullscreen detector, overlay seam, WebView2 package. Route: delegated writer. 54 files deleted (App/Alerts/**, App.Tests/Alerts/**, tint/shake/detector/overlay Interop + tests); WireProductionSettingsPersistenceWiringTests moved out of Alerts/. Player: TransferVideoFrame -> Present, parameterless ctor. Host keeps the swapchain tree, TryAttach, TaskbarCreated, slideshow re-raise. Retired alerts/alerts-enabled lines parse to defaults. WebView2 package and D2D1 NativeMethods removed. Checks: build --no-incremental 0 errors; Layout 198, Interop 235/38 skip, App 924/6 skip, 0 failed; rg sweep only retired-key comments/tests. Parent spot check: video player/host filter 4 passed/12 skipped (desktop-gated; real check in T7). Commit 6740e95. RDD: granted, refused lens_context_budget_exceeded -> no review (policy).
- [x] T6 README, docs and odd cleanup. Route: delegated writer. README documents only tiling, keybindings (ChordTable.cs), tray, video wallpaper, 5 settings keys, retired keys, note pointing to bk/cosmicwin-full and CielWin; mini video placeholder removed. Deleted 19 odd/tasks docs of removed features + docs/research/live-alert-wallpaper-plan.md; kept tiling/video docs (video-wallpaper-http-endpoint kept: cited by VideoWallpaperImport.cs). Untracked ignored docs/great-sage/ and docs/media/ left on disk (maintainer decides). Checks: build 0 errors; rg sweep justified.
- [x] T7 Hardware check: tray pick, Explorer restart re-attach, tiling intact. PARTIAL 2026-10-02 (Release of 7a300df launched elevated from the session scratchpad, run/ untouched, settings.conf backed up and left unchanged with its retired keys): startup trace `video-wallpaper phase=startup pathExists=True tryAttach=True tryPlay=True`; two desktop captures show different frames (video plays); Explorer killed and restarted (new explorer 13:57:50Z) -> capture shows the video playing again (re-attach OK); focus border/handover traces still flow. DONE 2026-10-02: maintainer turned tiling on (settings tiling = on; trace shows tiled columns with the 8 px gap, e.g. L=1700/W=838 and L=2546/W=838, H=1424); maintainer did the tray picks (tiny fixture, then back to the original via a temporary second hard link): three `video-wallpaper phase=pick pathExists=True tryAttach=True tryPlay=True` traces; final video-wallpaper.mp4 is the original 6.7 GB file, temporary link removed. Incident: an automation script's keystrokes landed in the maintainer's terminal (path typed as a bash command, plus Ctrl+A); reported to the maintainer.

## Acceptance / checks per task
- `dotnet build CosmicWin.sln` with zero errors; `dotnet test CosmicWin.sln` green (skips allowed as before).
- No dangling references (rg for removed type names returns nothing outside odd/ and the backup branch).
- Test-first exception: this is deletion; the RED/GREEN cycle does not apply. Proof = build + remaining suites green + rg sweep.

## Progress
T1-T7 done (commits in this branch's git log).

## Next step
Maintainer decides: merge feat/strip-to-tiling-video into main, copy the stripped build into run/, and whether to delete the ignored docs/great-sage/ and docs/media/.
