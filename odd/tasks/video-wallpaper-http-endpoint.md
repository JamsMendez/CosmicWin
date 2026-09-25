# Video wallpaper HTTP endpoint

## Objective

Let a local program switch the video wallpaper by sending an HTTP request with the path of a video
file. The result must match picking that MP4 from the tray menu.

## Why

Maintainer request, 2026-09-25. The existing HTTP alert endpoint already lets third-party apps on
this PC drive CosmicWin. Switching the wallpaper today needs the tray menu and a file dialog.

## Current state (mapped 2026-09-25, not yet implemented)

- HTTP server: `HttpAlertCommandServer` (`CosmicWin.Interop/HttpAlertCommandServer.cs`). It binds
  `http://127.0.0.1:{port}/` and `http://localhost:{port}/`, default port 47811
  (`AlertHttpProtocol.DefaultPort`). It serves only `POST /v1/alerts`, one request at a time on the
  `CosmicWin.AlertHttp` thread. The security gates run in `HandleRequest` in this order:
  1. loopback remote endpoint;
  2. reject any `Origin` header;
  3. `Host` must be exactly 127.0.0.1 or localhost with the port (the DNS-rebinding defense);
  4. path, then method;
  5. bearer token, compared in constant time;
  6. `Content-Type: application/json`;
  7. body at most 1024 bytes of strict UTF-8.
- Token: `AlertHttpTokenFile.LoadOrCreate`, stored in `%LOCALAPPDATA%\CosmicWin\alert-http.token`.
- `AppComposition` starts the server only when `alertsEnabled && alertHttpEnabled`, after the pipe.
- Tray pick: `TrayIconHost.PickVideoWallpaper` -> `TrayMenuController.SetVideoWallpaperPath` -> an
  INLINE closure in `AppComposition.Wire` (around lines 814-893). That closure posts ONE work item
  to `onVideoWallpaperThread` (the `MtaActionThread`), which runs these steps:
  1. `videoWallpaperPlayer.Stop()`;
  2. `VideoWallpaperImport.Import(path)`, a temp-then-move copy to `video-wallpaper<ext>`;
  3. `persistVideoWallpaperPath`;
  4. `ActivateVideoWallpaper("pick", imported)`.

  If the import fails, the previous video is restored. A failed pick never leaves the wallpaper
  dead.
- The only validation today is the file dialog: `*.mp4` and `CheckFileExists`. `Import` accepts any
  extension. An HTTP body gets neither check for free.

## Decided (maintainer, 2026-09-25)

- The request carries an ABSOLUTE path to a video file already on this PC. No downloads and no
  external URLs: an `http(s)://`, `file://` or any other URI scheme is rejected with 400. This
  settles part of decision 4 below: the path must be absolute and local.
- Decision 1: `POST /v1/wallpaper/video` with body `{"path":"C:\\...\\x.mp4"}`, on the SAME server,
  port, token and gates as `/v1/alerts`. No second server.
- Decision 2: a per-route switch. New settings key `video-wallpaper-http`, default off. The server
  starts when at least one route is on (`alerts && alert-http`, or `video-wallpaper-http`); a route
  whose switch is off answers 404, as if it did not exist. `alert-http` keeps its name.
- Decision 3: validate synchronously, answer `202 Accepted`, and run the copy and switch on the
  video wallpaper MTA thread, like the tray pick. Validation failures answer at once with their
  code. The final outcome (playing, or failed and restored) goes to the trace only.
- Decision 4: validation, all before answering:
  - the path is absolute and drive-rooted (`C:\...`); relative, drive-relative (`C:foo`), UNC,
    device (`\\?\`, `\\.\`) and any URI scheme answer 400;
  - the drive is not a network drive (a mapped letter is remote too): 400;
  - the file exists, otherwise 404;
  - the extension is `.mp4`, case-insensitive, otherwise 415;
  - the body limit is 4096 bytes for this route only; `/v1/alerts` keeps 1024.
- Decision 5: import by HARD LINK when the source is on the same volume as
  `%LOCALAPPDATA%\CosmicWin`, with the current temp-then-move COPY as the fallback (another volume,
  or the link fails). The goal is the lowest switch delay: a link is instant whatever the size. It
  lives inside `VideoWallpaperImport.Import`, so the tray pick gets it too; one code path. Known
  tradeoffs: an in-place edit of the source changes the wallpaper (same data), and deleting the
  source while it plays may fail with "file in use" depending on Media Foundation's share mode
  (unverified; measure it in the V5 hardware check).

## Tasks

- [x] V1 Extract the tray closure into one named operation, for example `SwitchVideoWallpaper(path)`,
  dispatched on `onVideoWallpaperThread`. The tray calls it, and its behaviour and tests do not
  change. This is a pure refactor, with existing wiring tests as the guard.
- [x] V1b `VideoWallpaperImport.Import`: hard link (to a temp name beside the destination, then the
  same move) when source and destination share a volume; fall back to the copy otherwise or when
  the link fails. Tests: same-volume link (destination shares the file identity, no bytes copied),
  cross-volume or link failure falls back to copy, the same-file short-circuit and the
  failure-leaves-previous-import guarantees still hold.
- [x] V1c (review follow-up, authorized 2026-09-25) Handle the held-source case of
  R3-linked-destination-inherits-source-sharing and add the same-source re-pick test. OUTCOME: the
  premise does not reproduce (see Progress), so no fallback ships; the tests and a temp sweep do.
- [x] V2 Protocol in Interop: route constant, JSON parse of `path`, validation, and a status code for
  each outcome (400 bad body or path, 404 file missing, 415 wrong extension, 202 accepted, 503 video
  wallpaper not available on this composition). Unit tests, as in `AlertHttpProtocolTests`.
- [x] V3 Server routing: `HttpAlertCommandServer` dispatches `/v1/wallpaper/video` to a new handler
  delegate after the SAME gates. Real-listener tests, as in `HttpAlertCommandServerTests`, including
  401 without a token and 403 for a foreign Origin or Host on the new route.
- [x] V4 Composition wiring: the handler calls the operation from V1 (never `Import` directly: imports
  must stay serialized), never blocks the HTTP thread,
  and traces the outcome without absolute paths. Wiring tests.
- [ ] V5 Docs: curl or PowerShell example with the token. Hardware check, driven by the agent:
  switch while playing, a missing file, a non-mp4 file, the 6.6 GB file (switch delay with the
  link), deleting the source while it plays, and two requests back to back. (The held-source
  sharing case is settled by V1c and needs no hardware check.)

## Constraints

- Everything stays local: no push, no gh.
- Strict TDD (runner `dotnet test`). Tests and docs go with each work unit.
- Hardware fillers are the agent's own probes, stopped by PID. Never touch Windows Terminal: the
  Claude session lives in it.
- The app runs elevated; the HTTP caller may not be. That is fine for loopback HTTP, but watch
  file-read permissions on the source path.

## Progress

Decisions 1-5 taken with the maintainer on 2026-09-25.

- V1 done (2026-09-25), commit `40efc84`. Route: delegated direct (writer trigger: reading that
  prepares a write in a 1600-line file). `SwitchVideoWallpaper(string path)` is a local function in
  `Wire`, right after `ActivateVideoWallpaper`; it null-checks host/player itself and posts the
  unchanged stop/import/persist/activate work item. The tray now calls it. The reconcile-tick and
  startup dispatches are different operations and were left alone. TDD: pure refactor, guarded by
  `VideoWallpaperPlaybackWiringTests` (restore on import failure, with and without a previous path).
  Checks: build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13,
  Interop 315/42 skipped, App 958/6 skipped, before and after; App re-run by the parent: 958/6.
  Review assess (base `ed85eeb`, committed-only): medium, `under_budget`, pending in the slice.
- V1b done (2026-09-25), commit `deb1a5a`. Route: delegated direct (writer trigger: source + tests).
  Link first through an injected `Func<string,string,bool>` seam (internal third `Import` overload),
  inline `CreateHardLinkW` P/Invoke, copy fallback unchanged. TDD: RED observed (`CS1501: No
  overload for method 'Import' takes 3 arguments`), then GREEN. Finding while writing it: a hard link
  never opens the source's data, so a `FileShare.None` lock no longer blocks the import; the one
  pre-existing test that relied on that lock now forces the link to fail through the seam. Checks:
  build clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13, Interop
  315/42 skipped, App 961/6 skipped; App re-run by the parent: 961/6. Parent check on disk: moving
  a fresh link over a destination that is already a link to the SAME file succeeds and removes the
  temp name (the same-source re-pick case).
- Review: assess (base `ed85eeb`) medium, `slice_budget_reached` (482 lines). Maintainer granted.
  Lens review-reliability, lineage `review-77ad03c2731f30e8`: APPROVED, acknowledged, authority
  burned. The reviewed boundary is now `deb1a5a`. Two advisory, non-blocking findings:
  - R3-linked-destination-inherits-source-sharing (WARNING): after a link, the destination IS the
    source's file object, so another process holding the source open without FILE_SHARE_DELETE (an
    editor, another player, a sync client) blocks the rename into place. The switch then falls back
    to restore, where a copy would have landed. The same hold on the PREVIOUS source blocks replacing
    the destination on a later switch.
  - R3-relink-same-source-untested (SUGGESTION): no unit test for re-picking the same original after
    a linked import (checked by hand on disk above, not in the suite).
  Both are proposed as follow-up V1c; not authorized yet.
- V1c done (2026-09-25), commits `b1bfe42` (writer) then `8846600` (simplified by the parent,
  maintainer's call). The writer's RED did not appear: with the source held open with
  `FileShare.Read`, and even `FileShare.None`, the real link AND the move into place both succeed.
  The parent re-measured it independently: moving another link of a held file works, and moving
  the held name itself fails (the control). NTFS checks delete sharing per opened NAME, not per
  file, so R3-linked-destination-inherits-source-sharing does not happen, including the
  previous-source case. The writer's link-then-move copy fallback (plus a fourth `moveIntoPlace`
  seam) therefore guarded a case Windows does not produce; the maintainer chose to drop it.
  Kept: `Import_WhenTheSourceIsHeldOpenWithoutFileShareDelete_TheLinkedMoveStillSucceeds` (a
  regression guard on the real behaviour), the same-source re-pick test, and a best-effort sweep of
  leftover `video-wallpaper*.tmp-*` files at the start of `Import`. Checks: build clean (3
  pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13, Interop 315/42 skipped,
  App 964/6 skipped. Assess (base `deb1a5a`): medium, 173 lines, `under_budget`, pending in the slice.
- V2 done (2026-09-25), commits `ed5438b` (writer) and `a1108b6` (parent fix). Route: delegated
  direct (writer trigger: new source + probes + tests). New `VideoWallpaperHttpProtocol` (Interop):
  `VideoPath = "/v1/wallpaper/video"`, `MaxBodyBytes = 4096`, `NotAvailableStatusCode = 503` for
  V4, `TryValidate(body, out path, out error, probes?)` returning `VideoWallpaperRequestOutcome`,
  and `StatusCodeFor` (202/400/404/415). Filesystem probes are injectable
  (`VideoWallpaperFileProbes`). Check order, cheapest first: JSON shape (unknown fields rejected,
  duplicate `path` last-wins), path syntax, `.mp4`, network drive, exists. So a missing `.txt`
  answers 415, never 404. Each error carries its own message, so a caller can tell this 404/415
  from the server's unknown-route and Content-Type ones. Error messages never echo the path.
  Writer's call, kept: `C:/x/y.mp4` (forward slashes) answers 400 (fail closed; decision 4 says
  `C:\...`). Parent review found and fixed a hole: a colon past the drive letter (an NTFS
  alternate data stream, `C:\x.txt:hidden.mp4`) passed as `.mp4`; now 400, checked after the URI
  and drive-rooted checks so those keep their specific reasons. TDD: writer RED `CS0246` (missing
  type); parent RED 2 failing ADS cases, then GREEN. Checks: build clean (3 pre-existing warnings);
  `dotnet test CosmicWin.sln` Layout 198, Alert 13, Interop 361/42 skipped, App 964/6 skipped.
- Review 2 (2026-09-25): assess (base `deb1a5a`) medium, `slice_budget_reached` (931 lines).
  Maintainer granted. Lens review-reliability, lineage `review-ece51ce347268439`: APPROVED,
  acknowledged, authority burned. The reviewed boundary is now `b92783f`. Two advisory findings:
  - R3-json-invalid-surrogate-escapes-throws (WARNING): CONFIRMED by a RED test. A lone
    `\uD800` escape is valid JSON, but reading it as a string throws InvalidOperationException,
    which escaped `TryValidate` AND the older `AlertHttpProtocol.TryTranslate` (through a key).
    Fixed in both, `d27f225`: now 400 "body is not valid JSON". Interop 364/42 skipped.
  - R3-temp-sweep-races-concurrent-import (WARNING): does not happen today. Every production
    import is serialized (inside SwitchVideoWallpaper's work item on the one video thread, or
    inline on the tray thread only when there is no video thread). Documented as a requirement
    at the sweep, `3dfbe44`. CONSTRAINT for V4: the HTTP handler must call SwitchVideoWallpaper
    (503 when it is unavailable), never `VideoWallpaperImport.Import` directly.
- V3 done (2026-09-25), commits `6678031` (writer) and `a580bfc` (parent fix). Route: delegated
  direct (writer trigger: server + real-listener tests). `HttpAlertCommandServer` gains trailing
  optional `handleVideoWallpaperSwitch: Func<string,bool>?` and `videoWallpaperProbes`;
  `handleCommand` becomes nullable. A null delegate means the route is off: it is left out of the
  route table, so it answers the same 404 "no such route" as an unknown path. Each route carries
  its own body cap (1024 alerts, 4096 video), chosen at the path gate before the body is read.
  The gate order is unchanged and shared (checked by the parent: loopback, Origin, Host, path,
  method, token, Content-Type, body). The video route validates, then calls the delegate: true
  answers 202 "ok", false answers 503 "error: video wallpaper is not available ...", and a throw
  answers 500 while the loop keeps serving. The `AppComposition` call site needed no change. Parent
  review fix: the throw diagnostic carried `exception.Message`, which for an I/O error names the
  absolute path; now it carries the type only (RED test first). Old test
  `Constructor_NullHandler_Throws` was replaced, because a null handler is now valid. Checks: build
  clean (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 198, Alert 13, Interop
  382/42 skipped, App 964/6 skipped. The class name `HttpAlertCommandServer` is now inaccurate; a
  rename is a later cosmetic call.
- Review 3 (2026-09-25): assess (base `b92783f`) medium, `slice_budget_reached` (618 lines).
  Maintainer granted. Lens review-reliability, lineage `review-cb7cd1e32be1714f`: APPROVED,
  acknowledged, authority burned. The reviewed boundary is now `9a056d9`. Two SUGGESTIONS, test
  coverage only, not yet authorized:
  - R3-001: `Constructor_NullHandlerAndNullVideoSwitch_DoesNotThrow_AndConstructsWithNoRoutesEnabled`
    only checks the constructor; nothing starts that server and shows both routes answer 404.
  - R3-002: the per-route cap also governs the chunked (no Content-Length) read loop, but the size
    tests only send a declared Content-Length. No test sends a chunked video body between 1024 and
    4096 (should pass) or over 4096 (should be 413 without draining).
- R3-001/R3-002 done (maintainer approved, 2026-09-25), commit `10d9481`, tests only. Two new
  tests start a server with both routes off and show `/v1/alerts` and `/v1/wallpaper/video` each
  answer exactly like an unknown path. Three raw-socket chunked tests (no Content-Length): 1237
  bytes pass the size gate on video but get 413 on alerts; over 4096 bytes get 413 on video without
  calling the switch delegate. Mutation checks, both reverted: registering the alert route with a
  null handler made the first new test fail (500 instead of 404); using the alert cap in the
  chunked loop made the video pass-through test fail (413 instead of 400). Interop 387/42 skipped
  (parent re-run).
- V4 done (2026-09-25), commits `11ea666` (setting) and `180fc24` (wiring). Route: delegated
  direct (writer trigger: settings + composition + three test files). `video-wallpaper-http`
  (default off) is parsed like `alert-http`, with a commented entry naming the shared port and
  token file. The HTTP server block moved OUT of the `alertsEnabled` block (the pipe and the alert
  preload stay inside). It starts when `(alerts && alert-http) || video-wallpaper-http`, and an off
  route is passed as a null delegate (404). `SwitchVideoWallpaper(path, phase = "pick")` now returns
  whether it posted; the HTTP delegate calls it with `phase: "http"`. It answers false (503) when
  there is no host/player, OR when the composition has no dedicated video thread, because the
  `onOwningThread` fallback runs inline and would do the copy on the HTTP thread. Production always
  wires the thread (`videoWallpaperThread.Post`, a `BlockingCollection`, safe to post from the HTTP
  thread; checked by the parent). New trace lines: `video-wallpaper phase=http ...` (no path) and
  `http-server start requested port=<p> alerts-route=<bool> video-route=<bool>`. The existing
  `alert-http start requested port=<p>` line is kept, emitted only when the alerts route is on. Tray
  traces are byte-identical. TDD: RED `CS1503` (factory seam signature). Checks: build clean (3
  pre-existing warnings); `dotnet test CosmicWin.sln` (parent re-run) Layout 198, Alert 13, Interop
  387/42 skipped, App 990/6 skipped. Assess (base `9a056d9`): medium, 683 lines, review due.
- Next: review 4, then V5 (docs + hardware check by the agent).
