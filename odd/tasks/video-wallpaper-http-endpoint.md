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

- [ ] V1 Extract the tray closure into one named operation, for example `SwitchVideoWallpaper(path)`,
  dispatched on `onVideoWallpaperThread`. The tray calls it, and its behaviour and tests do not
  change. This is a pure refactor, with existing wiring tests as the guard.
- [ ] V1b `VideoWallpaperImport.Import`: hard link (to a temp name beside the destination, then the
  same move) when source and destination share a volume; fall back to the copy otherwise or when
  the link fails. Tests: same-volume link (destination shares the file identity, no bytes copied),
  cross-volume or link failure falls back to copy, the same-file short-circuit and the
  failure-leaves-previous-import guarantees still hold.
- [ ] V2 Protocol in Interop: route constant, JSON parse of `path`, validation, and a status code for
  each outcome (400 bad body or path, 404 file missing, 415 wrong extension, 202 accepted, 503 video
  wallpaper not available on this composition). Unit tests, as in `AlertHttpProtocolTests`.
- [ ] V3 Server routing: `HttpAlertCommandServer` dispatches `/v1/wallpaper/video` to a new handler
  delegate after the SAME gates. Real-listener tests, as in `HttpAlertCommandServerTests`, including
  401 without a token and 403 for a foreign Origin or Host on the new route.
- [ ] V4 Composition wiring: the handler calls the operation from V1, never blocks the HTTP thread,
  and traces the outcome without absolute paths. Wiring tests.
- [ ] V5 Docs: curl or PowerShell example with the token. Hardware check, driven by the agent:
  switch while playing, a missing file, a non-mp4 file, the 6.6 GB file (switch delay with the
  link), deleting the source while it plays, and two requests back to back.

## Constraints

- Everything stays local: no push, no gh.
- Strict TDD (runner `dotnet test`). Tests and docs go with each work unit.
- Hardware fillers are the agent's own probes, stopped by PID. Never touch Windows Terminal: the
  Claude session lives in it.
- The app runs elevated; the HTTP caller may not be. That is fine for loopback HTTP, but watch
  file-read permissions on the source path.

## Progress

Decisions 1-5 taken with the maintainer on 2026-09-25. Next: V1.
