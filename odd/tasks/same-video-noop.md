# Same-video HTTP request is a no-op

## Objective

A repeated `POST /v1/wallpaper/video` naming the video that is already playing does nothing: the
video keeps playing without reloading or jumping back to the start. A different video still
switches exactly as today.

## Problem

`SwitchVideoWallpaper` (AppComposition.cs) always runs Stop -> import -> persist -> activate on the
video wallpaper thread. There is no same-video check, so a repeat request restarts playback.
`currentVideoWallpaperPath` holds the IMPORTED path (`%LOCALAPPDATA%\CosmicWin\video-wallpaper.<ext>`),
never the caller's source, so a string comparison cannot detect "same video".

## Decisions (maintainer, 2026-09-26)

1. HTTP only: a tray pick of the same video keeps reloading exactly as today.
2. Same response: the repeat still answers 202 like any accepted switch. `LocalHttpCommandServer` and
   the protocol do not change.

## Approach

- Same video = same NTFS file identity (volume serial + file index, `GetFileInformationByHandle`)
  of the requested source and the current imported destination. A hard-linked import IS the same
  file, and a request naming the imported destination itself matches trivially.
- Copy fallback (source on another volume): identities differ, so the request reloads, which is
  today's behavior. Accepted: no remembered source/size/timestamp heuristic, because a false
  "same" would silently ignore a real switch, while a false "different" only costs a reload.
- The check runs INSIDE the work item, BEFORE `Stop()`, on the http phase only, and only when
  `videoWallpaperActive` is true: if playback died, a repeat request revives it.
- Any identity failure (missing file, access denied) counts as "different" and switches as today.
- Trace `video-wallpaper phase=http unchanged` on a skip, so hardware runs can see it.

## Constraints

- Strict TDD, runner `dotnet test`. Mutation-check any test that passes on its first run.
- Everything stays local: no push, no gh.

## Tasks

- [x] S1 File-identity helper (`VideoWallpaperImport.IsSameFile` or similar) with tests: same path,
  hard link, distinct copy, missing file. Commit `0cbe2ca`.
- [x] S2 Wire the skip into the http path of `SwitchVideoWallpaper` through an injectable predicate
  with wiring tests: same+active skips (no Stop, no import, no persist, trace `unchanged`, still
  accepted), same+inactive reloads, different reloads, tray pick of the same video reloads.
  Commit `68f919d`.
- [ ] S3 Hardware check on the running build: two identical requests -> one `unchanged` trace, video
  does not restart; a different video still switches.

## Acceptance criteria

- Repeat HTTP request for the playing video: 202, no Stop/import/persist, trace `phase=http unchanged`.
- Tray pick of the same video, a different video, or a dead playback: unchanged behavior.
- All suites green.

## Progress

- 2026-09-26: branch `feat/same-video-http-noop` created from main 079dc3e. Route: S1+S2 delegated to
  one writer (2 non-trivial source files plus tests, writer trigger).
- 2026-09-26: S1+S2 done by the delegated writer, strict TDD throughout, runner `dotnet test`.

  **S1** (`CosmicWin.App/VideoWallpaperImport.cs`, `CosmicWin.App.Tests/VideoWallpaperImportTests.cs`,
  commit `0cbe2ca`): added `internal static bool IsSameFile(string, string)`, comparing NTFS volume
  serial + file index via `GetFileInformationByHandle` (opened read-only, sharing read/write/delete;
  any failure -> false, never throws). Four new tests: same path, hard-linked files (via the real
  `Import`), distinct copy with identical bytes (link forced to fail), missing file (both
  directions). RED: all four failed to compile (`CS0117: does not contain a definition for
  'IsSameFile'`) before the method existed. GREEN after implementing it: 19/19 passed in
  `VideoWallpaperImportTests` (15 pre-existing + 4 new). No first-run-pass tests, so no mutation
  check was required for S1.

  **S2** (`CosmicWin.App/AppComposition.cs`, `CosmicWin.App.Tests/VideoWallpaperPlaybackWiringTests.cs`,
  commit `68f919d`): `AppComposition.Wire` takes an optional `isSameVideoFile` predicate (null ->
  never same); `SwitchVideoWallpaper` takes an optional `skipIfUnchanged` flag, checked inside the
  work item before `Stop()` (`skipIfUnchanged && videoWallpaperActive.Value && currentVideoWallpaperPath
  is not null && isSameVideoFile(path, current)` -> trace `unchanged` and return; still returns
  `true`/202 to the caller). `HandleVideoWallpaperHttpSwitch` passes `skipIfUnchanged: true`; the
  tray's `setVideoWallpaperPath` closure is unchanged (never passes it). Production wiring passes
  `isSameVideoFile: VideoWallpaperImport.IsSameFile`. Four new wiring tests: same+active skips
  (no Stop/import/persist, trace `phase=http unchanged`, `accepted == true`), same+inactive (TryPlay
  failed at startup) reloads anyway, different path reloads, tray pick of the same video reloads
  regardless of the predicate. RED: all four failed to compile first (`CS1739`, no `isSameVideoFile`
  parameter); after adding the parameter, the three HTTP tests still failed once
  (`Assert.True(accepted)` got `False`) because they were missing `scheduleVideoWallpaperWork` --
  without a dedicated thread `HandleVideoWallpaperHttpSwitch` always answers `false` before the skip
  logic is ever reached; fixed by wiring a queue and draining the startup + switch work items, the
  same pattern the pre-existing HTTP tests use. GREEN after that fix: 30/30 passed in
  `VideoWallpaperPlaybackWiringTests` (26 pre-existing + 4 new). Mutation check on the skip
  condition (forced it to `false && ...`): `HttpSwitch_SamePathWhileActive_SkipsStopImportAnd...`
  failed as expected (`StopCallCount` 0 -> 1), then the condition was restored and the suite
  re-verified green.

  **Verification** (full solution, `dotnet build CosmicWin.sln --no-incremental` then
  `dotnet test CosmicWin.sln`): build succeeded with exactly the 3 pre-existing warnings (two
  `MultiMonitorWorkspaceAdapter.cs` nullability warnings in `CosmicWin.App`, one `CA2022` in
  `CosmicWinAlert.Tests`), no new warnings. All four test projects green:
  `CosmicWin.Layout.Tests` 198/198, `CosmicWinAlert.Tests` 13/13, `CosmicWin.Interop.Tests`
  387 passed / 42 skipped (pre-existing hardware-only skips) of 429, `CosmicWin.App.Tests`
  1020 passed / 6 skipped (pre-existing) of 1026.

  Decision made while implementing: S2's design note said "check ... before `Stop()`", read
  literally as inside the work item's closure, which is where it landed -- confirmed against the
  existing `previous`/`currentVideoWallpaperPath` read just below it, which is deliberately read
  inside the work item for the same ordering reason (queued work items can race a caller's stale
  read).

  S3 (hardware check) not started -- out of scope for this delegated writer per the task brief.
