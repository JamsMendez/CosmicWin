# Follow-ups of the same-video and alert busy-ignore features

## Objective

Close the three open review findings left by `same-video-noop.md` and `alert-busy-ignore.md`.

## Items

1. R3-inplace-edit-hardlink (same-video-noop): a video re-encoded or edited IN PLACE keeps its NTFS
   identity, so a repeat HTTP request is skipped as `unchanged` and never picks up the new content.
   DECIDED by the maintainer 2026-09-26: it must RELOAD.
2. R3-predicate-throw-not-contained (same-video-noop): an injected `isSameVideoFile` that throws
   escapes the video work item, while the comment claims any failure reads as "different".
3. R3-http-202-claim-unproved (alert-busy-ignore): the ignored alert's 202 is proven only through the
   pipe; no test drives the HTTP route.

## Approach

- Items 1 + 2 together. Comparing size/last-write of the requested path against the imported path
  CANNOT work: with a hard link both names are the same file, so they always agree. Instead take a
  SNAPSHOT (volume serial, file index, size, last-write time) of the file when playback starts
  successfully, and on an HTTP request compare a fresh snapshot of the REQUESTED path against it.
  Skip only when all four match and playback is active. The snapshot reader is wrapped so any
  exception reads as "no snapshot" = different = reload (item 2 disappears by construction).
- Item 3: a test that sends a second alert through the real HTTP route while one is showing and
  asserts 202 `ok` plus the `alert ignored` trace.

## Constraints

- Strict TDD, runner `dotnet test`. Mutation-check any test that passes on its first run.
- Decisions of the parent features stay: HTTP only for the video skip, same 202 response, tray
  re-pick always reloads, copy fallback reloads.
- Everything stays local: no push, no gh.

## Tasks

- [x] F1 Video snapshot: replace the identity-only check with the playback-start snapshot; exception
  -> reload. Tests: in-place edit (same identity, new size or last-write) reloads; unchanged file
  still skips; throwing reader reloads; existing same-video tests adapted (not weakened).
  Commit `0d20ae6`.
- [x] F2 HTTP-level test for an ignored alert (202 `ok`, trace `alert ignored`, never shown).
  Commit `f199ae5`.
- [ ] F3 Hardware: rewrite the playing video in place (touch last-write) and resend -> reload;
  resend without change -> `unchanged`. Out of scope for this delegated writer.

## Progress

- 2026-09-26: branch `fix/noop-followups` from main 001fcb3. Route: F1+F2 delegated to one writer.

- 2026-09-26: F1+F2 done by the delegated writer, strict TDD throughout, runner `dotnet test`.

  **F1** (`CosmicWin.App/VideoWallpaperImport.cs`, `CosmicWin.App/AppComposition.cs`,
  `CosmicWin.App.Tests/VideoWallpaperImportTests.cs`,
  `CosmicWin.App.Tests/VideoWallpaperPlaybackWiringTests.cs`, commit `0d20ae6`):

  `VideoWallpaperImport.IsSameFile` (identity-only: volume serial + file index) is replaced by a
  `public readonly record struct VideoFileSnapshot(uint VolumeSerialNumber, ulong FileIndex, long
  Size, long LastWriteTime)` and `internal static VideoFileSnapshot? TryReadSnapshot(string path)`
  (never throws, null on any failure). `VideoFileSnapshot` is `public` -- not `internal` like the
  rest of the class -- because it appears in `AppComposition.Wire`'s own public
  `readVideoFileSnapshot` parameter and C# requires a public member's signature to expose nothing
  less accessible than the member itself.

  `AppComposition.Wire`'s `isSameVideoFile: Func<string, string, bool>?` parameter is replaced by
  `readVideoFileSnapshot: Func<string, VideoWallpaperImport.VideoFileSnapshot?>?`. A new
  `currentVideoSnapshot` field is captured inside `ActivateVideoWallpaper` (covering every caller:
  startup, restore, and a successful switch) from the path that just (re)started, via a new
  `SafeReadSnapshot` wrapper that catches any exception from the injected reader and treats it as
  "no snapshot" -- resolving R3-predicate-throw-not-contained. It is cleared to null in the same
  place `videoWallpaperActive` itself is cleared (right after `Stop()`), so the two invariants never
  disagree. `SwitchVideoWallpaper`'s skip condition now compares a FRESH `SafeReadSnapshot` of the
  REQUESTED path against the STALE `currentVideoSnapshot` captured at playback start -- never a
  fresh-vs-fresh comparison, which a hard-linked import would always pass regardless of an in-place
  edit, since both names describe the same current bytes. This resolves R3-inplace-edit-hardlink.
  Production wiring: `readVideoFileSnapshot: VideoWallpaperImport.TryReadSnapshot`.

  RED (`dotnet test --filter FullyQualifiedName~VideoWallpaperImportTests`, production file
  stashed): 8 compile errors (`CS0117: 'VideoWallpaperImport' does not contain a definition for
  'TryReadSnapshot'`). GREEN after restoring the implementation and updating the wiring call site
  (a second, expected compile RED appeared first: `CS0117: ... does not contain a definition for
  'IsSameFile'` in `AppComposition.cs`, fixed by updating that call site too): 53/53 in both files.

  Tests adapted, not weakened (same scenarios, snapshot seam): `IsSameFile_SamePath_ReturnsTrue` ->
  `TryReadSnapshot_SamePathTwice_ReturnsEqualSnapshots`; `IsSameFile_HardLinkedFiles_ReturnsTrue` ->
  `TryReadSnapshot_HardLinkedFiles_ReturnsEqualSnapshots`;
  `IsSameFile_DistinctFilesWithIdenticalBytes_ReturnsFalse` ->
  `TryReadSnapshot_DistinctFilesWithIdenticalBytes_ReturnsDifferentSnapshots`;
  `IsSameFile_MissingFile_ReturnsFalse` -> `TryReadSnapshot_MissingFile_ReturnsNull`. New:
  `TryReadSnapshot_FileRewrittenInPlace_DiffersButKeepsTheSameIdentity` (writes to the hard-linked
  SOURCE after import, proves the destination's snapshot changes in size while its identity does
  not).

  Wiring tests (`VideoWallpaperPlaybackWiringTests.cs`) adapted to the `readVideoFileSnapshot` seam:
  `HttpSwitch_SamePathWhileActive_...`, `HttpSwitch_SamePathWhilePlaybackIsNotActive_ReloadsAnyway`,
  `TrayPick_SamePathWhileActive_ReloadsAnyway` now stub a constant snapshot instead of `(_, _) =>
  true`; `HttpSwitch_DifferentPathWhileActive_Reloads` stubs `_ => null` instead of `(_, _) =>
  false`. `HttpSwitch_ComparesTheRequestedPathAgainstTheCurrentImportedPath` (R3-predicate-args-
  unproved's own regression guard) is rewritten around a fake reader KEYED BY PATH returning a
  distinct snapshot per path except `repeatSource`, scripted to match `imported`'s snapshot (a
  hard-linked alias); it asserts the exact query order `[startupPath, firstSource, imported,
  repeatSource]`, proving the compare reads the REQUESTED path fresh each time and compares against
  the snapshot captured from the IMPORTED destination, never the raw caller-supplied source. Two new
  tests: `HttpSwitch_FileEditedInPlaceSincePlaybackStarted_Reloads` (a fake reader returns two
  different snapshots in sequence for the same identity, standing in for an edit between playback
  start and the repeat request) and `HttpSwitch_ThrowingSnapshotReader_ReloadsWithoutTheException
  Escaping` (a reader that throws on its second call).

  All five adapted/new wiring tests plus all six `TryReadSnapshot` tests passed on their first run
  after the implementation was restored (the seam rename alone made the pre-existing scenarios
  compile and pass again), so every one was mutation-checked:
  - `requestedSnapshot.Equals(activeSnapshot)` forced to `true`:
    `HttpSwitch_ComparesTheRequestedPathAgainstTheCurrentImportedPath` and
    `HttpSwitch_FileEditedInPlaceSincePlaybackStarted_Reloads` both failed (`StopCallCount` expected
    1, got 0); reverted, both green again.
  - `SafeReadSnapshot`'s try/catch removed: `HttpSwitch_ThrowingSnapshotReader_...` failed with the
    `IOException` escaping past `Assert.Null(thrown)`; reverted, green again.
  Full suite re-verified green after each revert.

  **F2** (`CosmicWin.App.Tests/Alerts/AlertHttpEndToEndTests.cs`, new file, commit `f199ae5`):
  drives `AppComposition.Wire`'s REAL `LocalHttpCommandServer` (`createLocalHttpCommandServer` left
  at its production default, so the real factory builds a real listener bound to a real loopback
  port from `GetFreePort()`) with a real `HttpClient`, mirroring
  `CosmicWin.Interop.Tests.LocalHttpCommandServerTests`'s own "integration: real transport,
  in-process server" idiom -- the same composition `HttpAlertCompositionWiringTests` exercises, but
  that file always substitutes a `FakeServer` for `createLocalHttpCommandServer`, so it can prove
  the wiring but never a genuine HTTP round trip. Only the named pipe is stubbed out (irrelevant to
  this file); the alert queue behind the HTTP route is the real, non-injectable `AlertQueue`.

  Two tests: `SecondAlertWhileFirstIsShowing_BothAnswer202Ok_ButOnlyTheFirstEverShows` (two real
  POSTs to `/v1/alerts` back to back, both 202 `ok`, trace contains `alert ignored`, only one
  `startAlertLayer` call ever recorded) and `SingleAlert_Answers202OkAndShows` (the negative
  control -- one alert, no collision, must still show through this same real-transport
  composition). No production code change was needed or made -- alert-busy-ignore's A1-A4 were
  already complete; this closes only the coverage gap R3-http-202-claim-unproved named.

  Both tests passed on first run, so both were mutation-checked: `AlertQueue.Enqueue`'s busy-ignore
  condition (`if (_current is { } active && ...)`) forced to `if (false && ...)` ->
  `SecondAlertWhileFirstIsShowing_...` failed (missing the `alert-http start requested` line inside
  its own "no unrelated failure" trace check, i.e. it never reached the ignore assertion the way it
  should -- reverted, green again); `UpdateAlertOverlay`'s `desktopVisible` forced to `false && ...`
  -> BOTH tests failed (`Assert.Single(events)` on an empty collection, since nothing ever shows
  when the desktop reads as never visible); reverted, both green again.

  **Verification** (full solution): `dotnet build CosmicWin.sln --no-incremental`: succeeded, 3
  warnings, exactly the pre-existing baseline (2 `MultiMonitorWorkspaceAdapter.cs` CS8604/CS8602 in
  `CosmicWin.App`, 1 `CosmicWinAlert.Tests/ProgramTests.cs` CA2022) -- no new warning.
  `dotnet test CosmicWin.sln`: `CosmicWin.Layout.Tests` 198/198 (baseline). `CosmicWinAlert.Tests`
  13/13 (baseline). `CosmicWin.Interop.Tests` 384 passed/42 skipped, 426 total (baseline).
  `CosmicWin.App.Tests` 1025 passed/6 skipped, 1031 total (baseline was 1020/6, 1026 total; +5 =
  F1's net +1 in `VideoWallpaperImportTests` (4 removed, 5 added) + net +2 in
  `VideoWallpaperPlaybackWiringTests` (0 removed, 2 added net of the rewritten operand test) + F2's
  2 new tests). All suites green, 0 failed.

  Status: **done** (F1, F2). F3 (hardware) intentionally out of scope for this delegated writer.

- 2026-09-26: review `review-49e89177bcb88372` (medium, reliability, main..977fcb9) APPROVED and
  acknowledged. Findings:
  - R3-f2-mutation-evidence-mismatch (WARNING): the F2 mutation record above named a trace check the
    committed test does not have (and counted six TryReadSnapshot tests; there are five). RE-VERIFIED
    by the parent: forcing the showing-ignore condition to `false && ...` in `AlertQueue.Enqueue`
    fails `SecondAlertWhileFirstIsShowing_BothAnswer202Ok_ButOnlyTheFirstEverShows` on
    `Assert.Contains` (no `alert ignored` line); restored. The earlier wording is superseded by this.
  - R3-snapshot-after-play-window (SUGGESTION): FIXED in `fe9428b` (route inline: one production line
    moved + one test). The baseline snapshot is now read BEFORE TryAttach/TryPlay. Test
    `HttpSwitch_FileEditedWhilePlaybackOpensIt_ReloadsOnTheRepeatRequest` (reader size = TryPlay
    count): RED before the fix (`StopCallCount` expected 1, actual 0: wrongly skipped), GREEN after.
    Full suites: Layout 198, Alert 13, Interop 384/42 skipped, App 1026/6 skipped.
  - R3-free-port-toctou (SUGGESTION): the F2 end-to-end tests pick a free port, release it, then
    let the composition bind it; another process can take it in between. Left open (test-only
    flakiness risk, no product impact).
