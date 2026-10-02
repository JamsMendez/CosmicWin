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
- [x] T2 (U2) Remove html mode + HTML scenes; settings collapse (no WallpaperMode/scene/fps). Route: delegated writer. 71 files deleted (Wallpaper/Web, scene tests, WebViewAlertLayerVisibility, WallpaperSceneHttpProtocol). Video plays whenever video-wallpaper-path is set; retired wallpaper-mode/scene/fps/mini-* lines parse to defaults (RetiredWallpaperLines_* tests). Alert controller serves only the alert page. Checks: build 0/0; Layout 198, CLI 13, Interop 511/42 skip, App 1248/6 skip, 0 failed; rg sweep empty. Review of the single 20k-line commit refused (lens_context_budget_exceeded), so T2 was split by maintainer choice: T2a = C#/tests (scene assets still on disk, dead; App 1253/6 skip incl. 5 scene node tests, 0 failed), T2b = delete the dead Wallpaper/Web assets, scene tests and their csproj Content items.
- [ ] T3 (U3) Remove HTTP server, token, routes, same-video snapshot no-op (~5.2k lines).
- [ ] T4 (U4) Remove named pipe + CosmicWinAlert(+Tests) projects (~1.8k lines).
- [ ] T5 (U5) Remove alert layer, tint, shake, fullscreen detector, overlay seam, WebView2 package (~10k lines).
- [ ] T6 README, docs and odd cleanup.
- [ ] T7 Hardware check: tray pick, Explorer restart re-attach, tiling intact.

## Acceptance / checks per task
- `dotnet build CosmicWin.sln` with zero errors; `dotnet test CosmicWin.sln` green (skips allowed as before).
- No dangling references (rg for removed type names returns nothing outside odd/ and the backup branch).
- Test-first exception: this is deletion; the RED/GREEN cycle does not apply. Proof = build + remaining suites green + rg sweep.

## Progress
T1, T2 done (commits in this branch's git log).

## Next step
T3.
