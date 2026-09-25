# Live alert wallpaper

> **Superseded (2026-09-24).** Replaced by the WebView2 alert layer
> (`odd/tasks/webview-alert-layer.md`). The Direct2D overlay, its frame-overlay seam and
> `AlertTileLayout` were deleted in `dc80b4f` (`odd/tasks/remove-direct2d-alert-overlay.md`).
> Kept as a historical record; the text below is unchanged.

## Objective

An external command such as `CosmicWin.exe --alert "warning:2 failed:1"` shows N alert tiles
(warning or failed) over the video wallpaper for a few seconds, then the video continues alone.

## Why

Maintainer request, 2026-09-23. Full analysis, measurements and design:
`docs/research/live-alert-wallpaper-plan.md`.

## Scope

In scope: primary monitor, kinds `warning` / `failed`, named-pipe command channel, alert layer
drawn natively with Direct2D / DirectWrite on the existing swapchain back buffer.
Out of scope: multi-monitor, WebView2 or any browser engine, HTTP listeners.

## Constraints

- Native Windows / .NET only, no third-party runtime dependency (route decided 2026-09-23:
  Direct2D, WebView2 rejected).
- `CosmicWin.Interop` is the only project touching Win32.
- Idle cost must stay zero: no drawing when no alert is on screen.
- A failing overlay must never break video playback.

## Decided: alerts while the desktop is covered

Maintainer, 2026-09-23: an alert that arrives while a fullscreen window covers the desktop is
**queued with a max age** (~5 min). It is shown once the desktop is visible again; past the max
age it is dropped with a log line. T2 implements it.

## TDD mode

**Strict TDD: enabled** — source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` per project. T0 is a throwaway hardware
spike, checked manually, not TDD.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: ~1200 authored lines over T1–T8. Chain strategy
chosen by the maintainer on 2026-09-23: **`stacked-to-main`**. RDD: on (global).

Slices (planned, each a PR against main stacked on the previous one):

1. `fix/explorer-restart-reattach` (base fix, already done).
2. T1 + T3: parser and tile layout (pure logic).
3. T2 + T4: queue and named pipe / `--alert` client.
4. T5 + T6: overlay seam and Direct2D overlay.
5. T7 + T8: visuals and wiring. T9 evidence goes with slice 5.

## Tasks

- [x] **T0 — Spike (gate):** throwaway branch `spike/alert-overlay-t0`. Direct2D rectangle and
  DirectWrite text drawn in `MediaFoundationVideoWallpaperPlayer.Tick()` between
  `TransferVideoFrame` and `Present`. Check on hardware: visible under the icons, survives the
  400 ms re-attach and an Explorer restart, GPU with and without the overlay (resolution and
  refresh recorded). Route: delegated writer (CsWin32 Direct2D interop, preparation reading),
  hardware run by the parent.
  **Partial, 2026-09-23.** Spike commit `d9fe4a2` on `spike/alert-overlay-t0` (not to be merged;
  `SpikeAlertOverlay.cs`, toggle `COSMICWIN_SPIKE_OVERLAY=1`). Hardware: RTX 4060 Ti,
  3440x1440 @ 164 Hz, back buffer 3392x1440 (primary work area). Evidence:
  - Renders: red translucent band and DirectWrite text drawn over the live video, taskbar above
    it. First draw logged `D2D1_ALPHA_MODE_PREMULTIPLIED`; no draw failure logged.
  - Kept rendering across the 400 ms watch tick for the whole run (> 1 min), no failure.
  - GPU 3D, same Debug build, visible desktop: overlay off 2.08 / 1.55 / 2.31 %, overlay on
    2.40 / 1.56 / 2.12 %. The overlay's cost is below the measurement noise. (The long-running
    Release instance read 6.15 / 6.69 / 6.55 % before the run; not the same build or state.)
  - Under the desktop icons: not verifiable, no icons are shown on this desktop.
  - **Explorer restart: BLOCKED by a pre-existing base bug.** After `Stop-Process explorer`, the
    video is gone in both runs, with the overlay on AND with it off (control). Window tree:
    `CosmicWinVideoWallpaperHost-*` is left as an orphaned top-level window, invisible,
    1x1, never re-parented to the new Progman. So the device re-creation path of the overlay
    could not be exercised. Not caused by the spike: with the variable unset `Draw()` returns on
    a bool.
  **Passed, 2026-09-23**, after the base bug was fixed on `fix/explorer-restart-reattach`
  (`322b8fb`, host recreated on the same D3D11 device). Spike branch with the fix cherry-picked
  (`13ff3ac`), overlay on, Explorer killed: new host `0x8046C` child of the new Progman
  `0x605C6`, 3392x1440, visible; the capture shows the video AND the overlay band drawn again;
  the log holds only the first-draw line, no failure. The device survives, so what this exercised
  is the overlay's back-buffer-keyed target rebuild. This feature branch is now stacked on
  `fix/explorer-restart-reattach`.
- [x] **T1 — `AlertCommandParser`.** `CosmicWin.App/Alerts/{AlertKind,AlertCommand,AlertCommandParser}.cs`,
  tests in `CosmicWin.App.Tests/Alerts/AlertCommandParserTests.cs`. Route: delegated writer
  (trigger: 2+ non-trivial files -- parser + shared record/enum types + tests). TDD: RED-1 (type
  missing) `error CS0234: The type or namespace name 'Alerts' does not exist`; stubbed
  `AlertCommandParser.Parse` to throw `NotImplementedException`, RED-2 27 failed / 0 passed, all
  `NotImplementedException`; GREEN after the real implementation, 27 passed / 0 failed. Grammar:
  whitespace-separated `kind:count` tokens (`warning`/`failed`, case-insensitive, count 1..16),
  optional `duration:seconds` (1..60, default 5s), each key at most once, at least one
  warning/failed group required, total tiles <= 16, input capped at 256 chars, order preserved,
  never throws -- bad input returns `AlertCommandParseResult.Fail(message)` naming the offending
  token. Commit `72e9397`.
- [x] **T2 — `AlertQueue`.** `CosmicWin.App/Alerts/AlertQueue.cs` (also defines `ActiveAlert`),
  tests in `CosmicWin.App.Tests/Alerts/AlertQueueTests.cs`. Route: delegated writer (trigger: 2+
  non-trivial files -- the queue plus its test file). TDD: RED-1 (type missing) `error CS0246: The
  type or namespace name 'AlertQueue' could not be found`, 13 occurrences; stubbed
  `AlertQueue.Enqueue`/`Advance` to throw `NotImplementedException`, RED-2 13 failed / 0 passed,
  all `NotImplementedException`; GREEN after the real implementation, 13/13 passed, full
  `CosmicWin.App.Tests` suite 960 passed / 0 failed / 6 skipped (pre-existing hardware-only skips).
  Implements the maintainer's covered-desktop decision above: bounded FIFO (capacity 8 default,
  constructor parameter), `Enqueue` rejects the NEW command when full and reports the rejection;
  `Advance(now, desktopVisible)` ends the current alert once `now >= StartedAt + Duration` (so it
  ends exactly at that instant, not one tick later), drops any queued alert that has waited
  strictly longer than the max age (5 min default, also a constructor parameter) regardless of
  visibility, and starts the next eligible one only when nothing is currently showing AND the
  desktop is visible -- so a covered desktop lets an already-showing alert's clock keep running
  (it ends on time, never paused/extended) but never starts a new one, and back-to-back alerts can
  start on the same `Advance` call the previous one ends on. Every rejection and every drop goes
  through an `Action<string>? onDiagnostic` constructor parameter, defaulting to a no-op -- the
  same optional-delegate convention `AppComposition`'s `persistX` parameters already use, chosen
  over a new interface since the message is a single short string. Documented as not thread-safe;
  the caller (a single-threaded render tick) serializes every call. Commit `2605623`.
- [x] **T3 — `AlertTileLayout`.** `CosmicWin.App/Alerts/AlertTileLayout.cs`, tests in
  `CosmicWin.App.Tests/Alerts/AlertTileLayoutTests.cs`. Route: delegated writer (trigger: 2+
  non-trivial files -- layout algorithm + its test file, alongside the T1 files already in the
  same task). TDD: RED-1 (type missing) `error CS0246: The type or namespace name 'Rectangle'
  could not be found`; stubbed `AlertTileLayout.Layout` to throw `NotImplementedException`, RED-2
  154 failed / 0 passed, all `NotImplementedException`; GREEN after the real implementation, 154
  passed / 0 failed (one REFACTOR-stage correction: a hand-computed expected pixel width in the
  N=1 test was arithmetically wrong -- 22112/9 floors to 2456, not 2457 -- caught by the failing
  assertion and fixed in the test, not the code). Full `Alerts` suite after both tasks: 181
  passed / 0 failed. Algorithm: tries every column count 1..N (rows = ceil(N/cols)), keeps the
  grid whose forced cell yields the largest 16:9 tile (tie -> fewer rows); outer margin and
  inter-tile gap both equal round(2% of the shorter area side); tiles fill row-major in command
  order, the grid is centered in the area, and a partial last row is centered under the rows
  above it. Reuses `CosmicWin.Interop.Rectangle` for the output type rather than adding a new
  one, matching `BorderGeometry`'s existing precedent of App-side pure geometry code depending on
  Interop's Win32-free `Rectangle`. Commit `4d5ffae`.
- [x] **T4 — Named pipe server and `CosmicWinAlert.exe` client.** Decision (maintainer,
  2026-09-23): `CosmicWin.App.exe` is `requireAdministrator` (`app.manifest:9`), so an `--alert`
  flag on it would raise UAC on every alert from an unelevated shell or WSL. The client is a
  separate `asInvoker` console exe instead. The elevated server's pipe needs a current-user ACL
  plus a medium mandatory integrity label so an unelevated client can write to it.
  Route: delegated writer (trigger: 2+ non-trivial files -- protocol/server/client each span a
  production file plus its tests, and the client is a whole new project). Naturally over the
  ~400-line heuristic (1232 authored lines across three commits): a wire protocol, a Win32 pipe
  server with an ACL builder, and a whole second executable with its own test project are three
  genuinely separate concerns the task explicitly asked for as one task, each already the smallest
  coherent unit it could be split into and each landed as its own work-unit commit.
  - **Protocol + pipe naming** (`CosmicWin.Interop/AlertPipeProtocol.cs`,
    `CosmicWin.Interop/AlertPipeName.cs`, pure, Win32-free, root namespace): 256-encoded-byte
    ceiling, `ok`/`error: ...` reply vocabulary, and the per-user pipe name from
    `WindowsIdentity.GetCurrent().User`. Put in `CosmicWin.Interop` rather than `CosmicWin.App`
    (departing from the plan's own "App, pure" heading) because `CosmicWinAlert` cannot reference
    `CosmicWin.App` (WPF + `requireAdministurator`) but can safely reference `CosmicWin.Interop`,
    which carries no Win32 at these two members. TDD: RED (type missing, stub throwing
    `NotImplementedException`) 11 failed / 4 passed (4 constant-only asserts needed no
    implementation); GREEN 15/15. Commit `7468f0d`.
  - **Server** (`CosmicWin.Interop/IAlertCommandServer.cs`,
    `CosmicWin.Interop/Win32/NamedPipeAlertCommandServer.cs`): one client at a time, one fresh
    per-user-ACLed pipe instance per connection, message-mode framing
    (`PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE`) so a single client `WriteFile` call is the whole
    message with no length prefix or delimiter needed, ~2 s per-connection read timeout, never
    throws out of the accept loop. **ACL approach:** CsWin32 `CreateNamedPipe` with a
    `SECURITY_ATTRIBUTES` built from `ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;<user-SID>)S:(ML;;NW;;;ME)")`
    -- chosen over the BCL `System.IO.Pipes.PipeSecurity`/`NamedPipeServerStreamAcl` because that
    type has no supported way to add a Mandatory Label ACE (a System ACL entry); only the SDDL
    conversion API builds both the DACL and the label from one string. The resulting native handle
    is wrapped as a `System.IO.Pipes.SafePipeHandle` and handed to the ordinary
    `NamedPipeServerStream(..., SafePipeHandle)` constructor for async read/write/timeout
    ergonomics -- CsWin32 only for the one call .NET cannot make (custom security descriptor +
    mandatory label), the managed type for everything else. ACL proven by reading it back live
    (`GetSecurityInfo` + `ConvertSecurityDescriptorToStringSecurityDescriptor`, not by re-asserting
    the SDDL string that went in) and asserting it contains the current user's SID and
    `(ML;;NW;;;ME)`. Two bugs found only by running the integration tests, not by inspection: (1)
    the raw `CreateNamedPipe` needs the full `\\.\pipe\<name>` path -- unlike
    `NamedPipeServerStream`/`NamedPipeClientStream`, which add that prefix themselves, it is not
    optional for the bare Win32 call; (2) a `NamedPipeServerStream` built from a raw handle does
    not learn `PIPE_READMODE_MESSAGE` from the handle -- `.ReadMode` must be set explicitly on both
    ends (`IsMessageComplete` throws `InvalidOperationException` otherwise); (3) a message that
    fits inside the read buffer but still exceeds the 256-byte wire limit needs an explicit length
    check alongside `IsMessageComplete`, since a fully-read message reports `IsMessageComplete =
    true` regardless of its own length. TDD: RED (types missing, then stubbed) 8 failed / 3 passed;
    GREEN, after fixing the three bugs above, 11/11; full `CosmicWin.Interop.Tests` suite 197
    passed / 0 failed / 34 skipped (pre-existing hardware-only skips). Commit `04a6a5a`.
  - **Client** (new project `CosmicWinAlert`, exe `CosmicWinAlert.exe`, `app.manifest` `asInvoker`,
    added to `CosmicWin.sln`; new test project `CosmicWinAlert.Tests`): joins its args with a
    single space, ~1 s connect timeout, exit 0 ok / 1 server error (message to stderr) / 2 no
    server or no reply in time / 3 usage error. References only `CosmicWin.Interop` (never
    `CosmicWin.App`), so it carries no WPF and no elevation manifest; needs no Win32 of its own
    since `NamedPipeClientStream` requires no ACL to connect. TDD: RED (type missing, then
    stubbed) 9 failed / 0 passed; GREEN 9/9 -- arg-joining and reply-to-exit-code mapping tested as
    pure functions, plus `RunAsync` end to end against a real `NamedPipeAlertCommandServer` on a
    unique test pipe name (no server / server-accepts / server-rejects / no-arguments), standing
    in for "one test that runs the built client end to end" more cheaply than spawning the actual
    `.exe`. Commit `f660adf`.
  - **Uncertain / not provable in-process:** the mandatory-label ACE was read back and asserted
    present with the right flags, but an actual cross-integrity-level round trip (an unelevated
    process writing to the elevated server's pipe) was not exercised here -- both the tests and
    this whole session run at one integrity level. T9's supervised hardware run is where that gets
    proven for real, ideally by running `CosmicWinAlert.exe` from an explicitly unelevated shell
    against the real elevated `CosmicWin.App.exe` once T8 wires the server in.
  - Not wired into `AppComposition` -- that is T8, as planned.
  **Cross-integrity check by the parent, 2026-09-23** (closes the writer's open point): a
  throwaway elevated harness hosted the real `NamedPipeAlertCommandServer` on
  `AlertPipeName.Resolve()`; `CosmicWinAlert.exe` launched through `explorer.exe` ran at
  `Medium Mandatory Level` (S-1-16-8192, from `whoami /groups`). `warning:2 failed:1` → server
  received it, reply `ok`, exit 0; `warning:2 bad:1` → `error: bad token`, exit 1. With no
  server: exit 2 after ~1.08 s; no arguments: usage line, exit 3.
- [x] **T4b — Harden the alert pipe and client.** Added 2026-09-23, maintainer accepted all six
  advisory findings of review `review-b48c85520820f775` (see Reviews). Checks: a test per finding,
  RED first. Route: delegated writer (trigger: 2+ non-trivial files -- server, protocol, client and
  queue each with their own tests, four commits across `CosmicWin.Interop`, `CosmicWin.App` and
  `CosmicWinAlert`). Naturally over the ~400-line heuristic (469 authored lines across four
  commits): each finding lives in a different file pairing (queue+tests, ACL test only,
  server+protocol+tests, client+tests) and none could be folded into another without mixing
  unrelated concerns in one commit.
  - **R3-queue-ctor-unvalidated** (`CosmicWin.App/Alerts/AlertQueue.cs`,
    `CosmicWin.App.Tests/Alerts/AlertQueueTests.cs`): constructor now throws
    `ArgumentOutOfRangeException` for `capacity <= 0` and for a negative `maxAge`; a zero `maxAge`
    is allowed on purpose (decided here) -- it means "must start the same tick it was enqueued, or
    be dropped," not a construction error, matching `DropExpired`'s existing strictly-greater-than
    boundary. TDD: RED 3 failed ("No exception was thrown") / 14 passed; GREEN 17/17. Commit
    `a89830b`.
  - **R3-acl-test-owner-masks-dacl** (`CosmicWin.Interop.Tests/Win32/NamedPipeAlertCommandServerAclTests.cs`,
    test-only): replaced the `Assert.Contains(userSid, sddl)` check (which would still pass if the
    SID appeared only as the SDDL owner, not the DACL) with a small regex parser that extracts the
    `D:` component's ACE list and asserts exactly one ACE, allow, for the current user SID, keeping
    the existing `S:(ML;;NW;;;ME)` label assertion. TDD: RED -- the new parser-based test, run
    against the unchanged production code, failed asserting `"GA"` (the generic right the SDDL
    string grants) where the live handle actually reports `"FA"` (the kernel resolves a generic
    right to its object-specific equivalent before storing the ACE); GREEN after correcting the
    test's expectation to `"FA"`, the real, directly observed mask, 3/3. Commit `427f7ac`.
  - **R3-client-unmapped-failures** (`CosmicWinAlert/Program.cs`, `CosmicWinAlert.Tests/ProgramTests.cs`):
    a zero-byte reply (server closed the connection without ever writing) now returns `ExitNoServer`
    (2) instead of falling through to `ExitServerError` (1); the connect/write/read round trip is
    now inside one try that also catches `IOException`/`UnauthorizedAccessException` (broken pipe
    mid-call, access denied, "all pipe instances are busy") and maps them to `ExitNoServer` with one
    stderr line, instead of letting them escape `Main` unhandled. TDD: RED 2 failed -- zero-byte
    case returned 1 instead of 2; broken-pipe case threw an unhandled `IOException` out of the test
    itself; GREEN 11/11 (full `CosmicWinAlert.Tests` suite, run three times, stable). Commit
    `9208efd`.
  - **R3-reply-drain-unbounded**, **R3-runloop-hot-spin**, **R3-oversized-reply-size**
    (`CosmicWin.Interop/Win32/NamedPipeAlertCommandServer.cs`, `CosmicWin.Interop/AlertPipeProtocol.cs`,
    their tests): three findings in one commit since they share the same two production files and
    were developed and verified together.
    - Reply drain: `WriteReplyAsync`'s write+flush+`WaitForPipeDrain()` sequence is now bounded by a
      new `ReplyTimeout` (2s). `WaitForPipeDrain` has no cancellable overload, so it runs on a
      background `Task.Run` raced against the timeout; on timeout the connection is forced closed
      with `NamedPipeServerStream.Disconnect()`, which was verified live to actually unblock the
      still-running native `WaitForPipeDrain`/`FlushFileBuffers` call and free the
      `nMaxInstances = 1` pipe instance for the next client -- `Dispose()` alone does not do this,
      since the pending native call keeps the handle referenced until it returns; this was the one
      genuinely uncertain part of the fix and is now confirmed by a passing integration test, not
      assumed. TDD: RED (compile error, `ReplyTimeout` missing); GREEN on the first real run after
      implementing the bounded drain + forced disconnect -- both the "second client still served"
      and the "Dispose stays prompt while stuck" tests passed immediately.
    - RunLoop backoff: a RunLoop-level failure (chiefly a pipe-name collision with another server
      instance on the same name) now waits `InitialRetryBackoff` (100ms), doubling up to
      `MaxRetryBackoff` (5s), reset to 100ms the moment a connection is served again; since the
      backoff wait and the diagnostic call are the same event, bounding the retry rate bounds the
      diagnostic rate for free, with no separate coalescing mechanism needed. TDD: RED -- two
      servers on one colliding pipe name logged **132,391** diagnostics in a 3-second window (a
      genuine, measured hot spin) while the healthy first server still served correctly; GREEN with
      the backoff in place, same scenario, diagnostic count bounded (asserted `<= 25` over the same
      3s window) and the first server still served correctly.
    - Oversized reply size: a message too large to fit `ReadBufferSize` used to report the
      *truncated* read count as if it were the message's real size. `AlertPipeProtocol` gained
      `OversizedReplyAtLeast(int)`, an honest lower-bound reply ("more than N bytes"), used when
      `!IsMessageComplete`; the existing `OversizedReply(int)` (exact count) still applies when the
      whole message fit under `ReadBufferSize` but exceeded the wire limit, since `read` is accurate
      in that case. TDD: RED -- a message larger than `ReadBufferSize` got the old exact-but-wrong
      reply; GREEN after the fix, 1/1 new test plus the full `AlertPipeProtocolTests` /
      `NamedPipeAlertCommandServerTests` suites (20/20).
    - `ReadBufferSize` changed from `private` to `internal const` so the new oversized-message test
      can size its payload without duplicating the constant.
    - Commit `e4abf30`.
  - **Verification**: `dotnet build CosmicWin.sln -c Debug` 0 errors; `CosmicWin.App.Tests` 964
    passed / 0 failed / 6 skipped; `CosmicWin.Interop.Tests` 202 passed / 0 failed / 34 skipped;
    `CosmicWinAlert.Tests` 11 passed / 0 failed. The pipe-touching classes
    (`NamedPipeAlertCommandServerTests`, `NamedPipeAlertCommandServerAclTests`,
    `CosmicWinAlert.Tests`) were each run three times end to end: stable every time, no flakes.
- [x] **T4c — Correct the three defects T4b introduced.** Added 2026-09-23, maintainer accepted
  the three advisory findings of review `review-c9bc26f67897ef30` as one bounded correction:
  restore the "not running" message on connect timeout (test asserts stderr), make the drain test
  keep the rude client connected and not reading (RED against the unbounded drain), cap the
  backoff below the client's connect budget and reset it after any accepted connection. Route:
  delegated writer (same writer as T4b). 266 authored lines across three commits.
  - **R3-client-connect-timeout-message-regressed** (`CosmicWinAlert/Program.cs`,
    `CosmicWinAlert.Tests/ProgramTests.cs`): T4b's client-exit-code fix had folded connecting and
    the write/read round trip into one shared `try`, so a connect timeout (no server listening)
    started printing "did not reply in time" instead of the original "not running" message; the
    exit code (2) was never wrong. Split back into two `try` blocks, each with its own message --
    "not running" for `ConnectAsync` failures/timeouts, "did not reply in time" only for a timeout
    AFTER a successful connect. TDD: RED 1 failed (`RunAsync_NoServerListening_...` got "did not
    reply in time" instead of "not running"; the paired "connected but never replies" test already
    passed, since both cases happened to share text under the bug) / 1 passed; GREEN 13/13 (full
    `CosmicWinAlert.Tests`, two new stderr-text assertions). Commit `91f5783`.
  - **R3-drain-test-does-not-prove-timeout** (`CosmicWin.Interop.Tests/Win32/NamedPipeAlertCommandServerTests.cs`,
    test-only): the original drain test disposed the rude client BEFORE the second client
    connected -- disposing breaks the pipe by itself, so the OLD unbounded `WaitForPipeDrain` would
    have passed that version too, proving nothing about `ReplyTimeout` specifically. Rewrote it so
    the rude client stays connected and non-reading for the WHOLE test, including while the second
    client connects and must be served within `ReplyTimeout + 3s`. TDD, proven per the coordinator's
    instructions by temporarily reverting the bound: with `WriteReplyAsync`'s bounded drain
    temporarily replaced by the bare pre-fix `pipe.WaitForPipeDrain()` call (reverted immediately
    after, never committed), the rewritten test failed -- `OperationCanceledException` from the
    second client's own 5s connect budget, i.e. it never got served -- RED confirmed the test
    actually exercises the bound; with the real bound restored, GREEN (same test, 1/1), then the
    full pipe suite 15/15. Commit `fe10136`.
  - **R3-backoff-ceiling-exceeds-client-connect-timeout** (`CosmicWin.Interop/Win32/NamedPipeAlertCommandServer.cs`,
    its tests): `MaxRetryBackoff` lowered from 5s to 500ms (ample headroom under
    `CosmicWinAlert.Program.ConnectTimeout`'s ~1s), and `ServeOneConnectionAsync` now takes an
    `onConnectionAccepted` callback invoked immediately after `WaitForConnectionAsync` succeeds, so
    `RunLoop` resets the backoff at the moment of ACCEPTANCE rather than only once
    `ServeOneConnectionAsync` returns without throwing (which today only happens after a full
    serve, though nothing between acceptance and return could actually throw -- this makes the
    reset point explicit and robust to that changing later). TDD: RED -- a new test occupies a pipe
    name with a "blocker" server, lets a second server fail against the collision for a 4s burst,
    disposes the blocker, then asserts a real client can connect within its own 1s budget; against
    the unfixed 5s-cap/served-only-reset code this failed deterministically
    (`OperationCanceledException`, the client's 1s budget expired); GREEN after the fix, same test,
    plus the full `NamedPipeAlertCommandServerTests`/`NamedPipeAlertCommandServerAclTests` suite
    (16/16) -- including the existing hot-spin diagnostics-count test, still comfortably within its
    `[1, 25]` bound under the new lower cap. Commit `94fae54`.
  - **Verification**: `dotnet build CosmicWin.sln -c Debug` 0 errors; `CosmicWin.App.Tests` 964
    passed / 0 failed / 6 skipped; `CosmicWin.Interop.Tests` 203 passed / 0 failed / 34 skipped;
    `CosmicWinAlert.Tests` 13 passed / 0 failed. Pipe-touching classes
    (`NamedPipeAlertCommandServerTests`, `NamedPipeAlertCommandServerAclTests`,
    `CosmicWinAlert.Tests`) each run three times end to end: stable every time (16/16 and 13/13
    every run), no flakes. `CosmicWin.App.exe` was not stopped or started.
- [x] **T5 — `IFrameOverlay` seam in the player.** Added public Interop seam
  `CosmicWin.Interop/IFrameOverlay.cs` with internal `Draw(ID3D11Texture2D, RECT)`, optional
  constructor injection into `MediaFoundationVideoWallpaperPlayer`, a private no-op default, and a
  call between `TransferVideoFrame` and `Present`. Overlay exceptions are caught separately so a
  bad overlay cannot stop the already-transferred frame from being presented; other tick failures
  stay contained by the existing pump guard. TDD: RED (type missing)
  `CS0246: IFrameOverlay could not be found`; GREEN
  `MediaFoundationVideoWallpaperPlayerFrameOverlayTests` 3/3. Verification:
  `MediaFoundationVideoWallpaperPlayer` filter 7 passed / 4 skipped; full
  `CosmicWin.Interop.Tests` 206 passed / 34 skipped. Independent verifier: approved for T5, no
  blocking findings. RDD assess could not score the untracked candidate directly, so it failed
  closed to unassessable/high and the required independent verifier was run.
- [x] **T6 — `Direct2DAlertOverlay`.** Added `FrameOverlayTile` / `FrameOverlayTileKind`
  as the Interop-owned tile snapshot API for T8, and `Direct2DAlertOverlay` in
  `CosmicWin.Interop.Win32` implementing `IFrameOverlay` with `SetTiles`, `Clear`, `TileCount`,
  lazy Direct2D/DirectWrite resource creation only when non-idle, D3D device identity rebuild,
  back-buffer target bitmap rebuild, alpha-mode fallback, throttled first-failure logging, COM
  cleanup on `Dispose`, and simple warning/failed filled tiles with centered labels. Resource
  failures are contained by `Draw`, and partial initialization now assigns COM objects directly to
  owning fields so `ReleaseAllResources()` cleans up failed setup attempts. TDD: RED (types
  missing) for `Direct2DAlertOverlay`, `FrameOverlayTile`, `FrameOverlayTileKind`; GREEN
  `Direct2DAlertOverlay` focused tests 2/2. Verification: `dotnet build CosmicWin.sln -c Debug`
  0 warnings / 0 errors after changing `NativeMethods.txt` to enum type names;
  `CosmicWin.Interop.Tests` 208 passed / 34 skipped. Independent verifier first found the partial
  COM-initialization leak risk; after remediation it approved T6 code-side. Native review
  `review-ba2d4e3013e17c30` (reliability) approved and was acknowledged; advisory findings are
  recorded in Reviews. Commit `c2515c7`. Manual/hardware gap: actual Direct2D drawing, alpha
  fallback, Explorer-restart rebuild, and GPU cost still await T9.
- [x] **T7 — Alert visuals ported from great-sage.** Added timing data to
  `FrameOverlayTile` (`StartedAt`, `Duration`) for T8 mapping and ported the great-sage alert
  model into `Direct2DAlertOverlay`: failed/warning themes, 230 ms failed shake state, 700 ms
  reveal progress, 100 ms module counter, full-tile wash, inner frame, segmented rails, large title
  fragments, side binary modules, and simple moving intersection-band accents. Warning skips shake
  and backdrop pixelation; failed exposes shake/pixelate state but does not move the video/window.
  Native v1 approximates canvas compositing with Direct2D primitives: no true backdrop pixelation,
  no browser/offscreen masking, and title inversion/intersection clipping are approximate and left
  for T9 visual review. Strict TDD: RED focused `Direct2DAlertOverlay` test compile failed on
  missing `FrameOverlayTile.StartedAt`/`Duration` and `ComputeVisualStateForTests`; GREEN focused
  filter 5/5 passed. Verification: `dotnet build CosmicWin.sln -c Debug` 0 warnings / 0 errors;
  full `CosmicWin.Interop.Tests` 211 passed / 0 failed / 34 skipped. Native review
  `review-dd0ba74af27e9322` (reliability) approved and was acknowledged; advisory findings are
  recorded in Reviews.
- [x] **T8 — Wiring and `alerts-enabled` setting.** Added `alerts-enabled` to
  `Settings` (default on, `on/off/true/false/1/0`, serialized with a comment) and wired production
  so an enabled run constructs one `Direct2DAlertOverlay`, passes it to
  `MediaFoundationVideoWallpaperPlayer(alertOverlay)`, starts the per-user
  `NamedPipeAlertCommandServer`, parses incoming commands, returns protocol `ok` / `error`, enqueues
  accepted commands into `AlertQueue`, and updates/clears overlay tiles on the existing 400 ms watch
  tick via `AlertTileLayout`. Queue mutation is guarded by a lock because the pipe server callback
  and watch tick can be on different threads. Desktop visibility uses the conservative T8 seam
  `videoWallpaperActive` (or injected test predicate); real covered-desktop behavior remains for
  T9. Disposal now stops the alert server and overlay with the app/video wallpaper path. Strict
  TDD: RED focused settings/wiring tests failed on missing `Settings.AlertsEnabled` and missing
  `AppComposition.Wire(alertsEnabled: ...)`; GREEN focused settings + alert wiring tests 74/74.
  Verification: `CosmicWin.App.Tests` 983 passed / 6 skipped; `dotnet build CosmicWin.sln -c
  Debug` 0 warnings / 0 errors. Native review `review-13be75f36453d741` (reliability) required a
  one-line correction adding an explicit alerts namespace import to the new test file; targeted
  validation then approved and was acknowledged. Advisory findings are recorded in Reviews.
- [x] **T9 — Supervised hardware run** (2026-09-23, agent-driven, elevated shell, 3440x1440
  primary, app launched from `run\` via `scripts/run.ps1`). Screenshots were taken with the
  maintainer's windows minimized and restored afterwards. Results:
  - Combined `warning:2 failed:1`: three tiles in command order at 2 s, cleared by 7 s. **Pass.**
  - Back-to-back `failed:1 duration:3` then `warning:6 duration:3` (65 ms apart): the failed tile
    showed first (1.2 s), the six warnings after it (4 s), clear at 7.5 s -- FIFO, one at a time.
    **Pass.**
  - Malformed `bogus:3` and `failed:17`: client exit 1 with a one-line reason. **Pass.**
  - Explorer restart (`Stop-Process explorer`): video re-attached, a later `warning:1 failed:3`
    drew correctly and cleared. **Pass.**
  - GPU (`\GPU Engine(*engtype_3D)` summed for the app PID): idle 7.08 %, 16-tile
    `failed:8 warning:8` 11.18 %, after clear 6.81 %. Idle cost returns to baseline. **Pass.**
  - `alerts-enabled = off` + app restart: client prints "not running, or is not listening" and
    exits 2; restoring the setting and restarting accepts alerts again. **Pass.** (App was stopped
    with `Stop-Process -Force`, so graceful tray-exit disposal was not exercised.)
  - No `alert-overlay.log` was created during the whole run: no overlay draw/resource failures.
  - **Covered desktop: FAIL.** A borderless topmost fullscreen probe covered the primary monitor,
    `failed:2 duration:4` was sent under it (exit 0), the probe closed 5 s later: no tiles ever
    appeared. Cause (code): production passes `desktopVisible: videoWallpaperActive`
    (`AppComposition.cs` ~line 416), which stays true under a fullscreen window, so the queue plays
    the alert out unseen instead of holding it (decision of 2026-09-23). Tracked as T10.
- [x] **T10 — Real desktop-visibility predicate for the alert queue.** Added
  `CosmicWin.Interop.Win32.PrimaryMonitorFullscreenDetector`: a pure `IsFullscreen(style, bounds,
  monitor)` classifier reusing -- not re-inventing -- commit a023fac's own fullscreen definition (no
  caption, not maximised, covering to within two pixels per edge), duplicated as documented
  constants only because `CosmicWin.Interop` takes no project references and so cannot depend on
  `CosmicWin.Layout.Filters.WindowStyleFlags`, plus the real Win32 entry point
  `IsPrimaryMonitorCoveredByFullscreenWindow()` (foreground window vs the primary monitor -- decided
  over enumerating every top-level window: what actually hides the desktop is whatever the user is
  looking at, and this is exactly the shape of the T9 probe, a borderless TOPMOST form that had just
  taken focus). `AppComposition.Wire` gained an `isPrimaryMonitorCovered` seam, composed with the
  existing `videoWallpaperActive` flag into the real `desktopVisible` predicate
  (`alertDesktopVisible`, when supplied, still overrides the whole composition -- the seam every
  earlier alert test uses); `WireProduction` wires the real detector. TDD: RED
  `PrimaryMonitorFullscreenDetectorTests` (type missing), GREEN 7/7; RED
  `AlertDesktopVisibilityWiringTests` (covered desktop still showed the alert -- the T9 defect,
  reproduced), GREEN 3/3 (covered holds, uncovered shows, queued-while-covered shows on uncover).
  Commit `613d062`.
- [x] **T11 — Video covers the whole primary monitor, not its work area.** Renamed
  `Win32VideoWallpaperHost.GetPrimaryWorkArea` to `GetPrimaryMonitorRect` and sized
  `CreateHostWindow` to `rcMonitor` instead of `rcWork` -- the swapchain/back buffer already derive
  their size from `GetWindowRect` on this same window (`CreateSwapChainAndPresentTestPattern` /
  `CreateSwapChainOnExistingDevice`), so this one change covers both the first attach and the
  Explorer-restart recreation path (`RecreateDestroyedHostWindow`). Alert tiles stay confined to the
  work area: added `AlertTileLayout.ToBackBufferCoordinates(tiles, offsetX, offsetY)`, a pure
  translation, and `AppComposition` now offsets the work-area-local layout by
  `primary.WorkArea.Left/Top - primary.Bounds.Left/Top` before handing tiles to the overlay, which
  draws in back-buffer coordinates. TDD: RED `AlertTileLayoutTests` (`ToBackBufferCoordinates`
  missing), GREEN 160/160 (`App.Tests` full run at the time); RED
  `ValidAlertCommand_TilesAreOffsetByTheWorkAreasOriginOnTheMonitor` (a taskbar-docked-top fixture,
  `tile.Bounds.Top=21` instead of `>= 40`), GREEN. A new `[RequiresDesktopSessionFact]`,
  `TryAttach_SizesTheHostWindowToTheWholePrimaryMonitorNotJustTheWorkArea`, asserts host rect ==
  monitor rect; run with `COSMICWIN_RUN_DESKTOP_TESTS=1` it **skipped**, exactly because
  `CosmicWin.App.exe` (PID confirmed live, per the hard rule not to touch it) is running --
  `DesktopGate.SessionSkipReason` correctly refuses a fact that would spawn a window on top of the
  running app rather than reporting a false pass or a false fail. Hardware confirmation (host rect,
  no black bars) is therefore still open -- see Next step. Commit `bc017bc`.

Route (T10 + T11): delegated direct, one writer, sequential commits -- writer trigger (host +
test, AppComposition + visibility source + tests) and preparation trigger (a023fac detection).

Task details and checks: plan §6.

## Progress

2026-09-23: branch `feat/live-alert-wallpaper` created, T0 started. Engram mirror `odd/live-alert-wallpaper/tasks`: **pending** (save refused, several active sessions matched).

2026-09-23: T0 partial: Direct2D on the back buffer works and costs nothing measurable; the
Explorer-restart leg is blocked by a pre-existing re-attach bug (host orphaned).

2026-09-23: base bug fixed (`fix/explorer-restart-reattach`); T0 passed. Branch rebased onto it.

2026-09-23: T4 done -- alert pipe protocol, named-pipe server with the per-user ACL + medium
mandatory label, and the unelevated `CosmicWinAlert.exe` client, each its own commit
(`7468f0d`, `04a6a5a`, `f660adf`). Not wired into `AppComposition` yet (T8).

2026-09-23: T4b done -- all six advisory findings from review `review-b48c85520820f775` fixed with
a RED test per finding (see T4b entry for evidence): bounded reply drain + forced disconnect,
exponential RunLoop backoff, honest oversized-reply size, mapped client exit codes, exact-DACL
test, and `AlertQueue` constructor validation. Four commits (`a89830b`, `427f7ac`, `9208efd`,
`e4abf30`), 469 authored lines. Full verification green three times over for the pipe-touching
test classes; no flakes.

2026-09-23: T4c done -- the three defects review `review-c9bc26f67897ef30` found in T4b's own
change, fixed as one bounded correction with a RED test per defect (see T4c entry for evidence):
restored the connect-timeout "not running" message, rewrote the drain test to actually prove the
bound (RED demonstrated by temporarily reverting the bound), and capped the RunLoop backoff under
the client's connect budget with an accept-time reset. Three commits (`91f5783`, `fe10136`,
`94fae54`), 266 authored lines. Full verification green three times over for the pipe-touching
test classes; no flakes.

2026-09-23: T5 done -- `IFrameOverlay` seam added to the player, with optional injection, no-op
default, draw after video transfer and before present, and isolated overlay exception containment.
TDD RED/GREEN and full Interop verification green; independent verifier approved T5.

2026-09-23: T6 done -- `Direct2DAlertOverlay` code-side landed with the Interop tile snapshot API,
lazy D2D/DWrite resource lifecycle, device/back-buffer rebuild, failure cleanup/logging, and simple
plain tile drawing. Focused tests and full Interop tests green; independent verifier approved after
one lifecycle cleanup fix. Hardware/runtime drawing evidence remains for T9.

2026-09-23: T7 done -- great-sage alert visuals ported into the Direct2D overlay with timing state,
warning/failed themes, title fragments, rails, modules, and moving band accents. Focused tests,
build, full Interop tests, and native review all green; visual fidelity remains for T9 hardware
comparison.

2026-09-23: T8 done -- `alerts-enabled` setting and production wiring are in place: named-pipe
commands parse/enqueue, the 400 ms tick advances the queue and maps active alerts through layout to
`Direct2DAlertOverlay`, and disposal owns the server/overlay. Focused and full App tests green;
native review approved after the one-line test import correction.

2026-09-23: T9 (supervised hardware run) done -- see its own task entry above for the full pass/fail
list. Every scenario passed except covered-desktop, tracked as T10.

2026-09-23: T11 done -- `Win32VideoWallpaperHost` sizes the host to the whole primary monitor
(`rcMonitor`) instead of its work area, fixing the ultrawide letterboxing T9's plan anticipated;
alert tiles are re-offset into back-buffer coordinates so they stay clear of the taskbar. TDD
RED/GREEN throughout; full `CosmicWin.Interop.Tests` (218/0/35) and `CosmicWin.App.Tests`
(990/0/6) green. The new real-attach fact SKIPPED under `COSMICWIN_RUN_DESKTOP_TESTS=1` because
`CosmicWin.App.exe` is running (the hard rule against touching it) -- hardware confirmation is
still open. Commit `bc017bc`.

2026-09-23: T10 done -- `PrimaryMonitorFullscreenDetector` (Interop) reuses a023fac's own
fullscreen definition to answer "is the desktop covered", and `AppComposition`'s alert predicate
now composes it with `videoWallpaperActive` instead of using the video flag alone (T9's finding).
TDD RED/GREEN throughout (a wiring test reproduces the T9 covered-desktop defect RED, then proves
hold-while-covered/show-on-uncover GREEN). Full `CosmicWin.Interop.Tests` (218/0/35) and
`CosmicWin.App.Tests` (990/0/6) green. Commit `613d062`. The T9 covered-desktop hardware probe
itself still needs re-running (see Next step).

## Reviews

- Slice fix + T1 + T3 (`--base-ref fadfda7`, 1373 lines, risk `medium`, `slice_budget_reached`):
  consent granted by the maintainer; lineage `review-cd5abe02fe1d9247`, one lens
  (reliability), **approved**, acknowledged, authority burned. Reviewed boundary is now `e87a108`.
  Advisory, non-blocking findings (follow-up work, not reopened):
  - `R3-device-rcw-release` (WARNING, inferential): a second Explorer restart could lose the
    device. Checked on hardware the same day: three consecutive Explorer restarts, the host was
    recreated under each new Progman (`0xC0412`, `0x509E0`, `0x50A0A`) and the video kept
    playing. Not reproduced.
  - `R3-concurrent-swapchain-release` (WARNING, inferential): swapchain released on the wallpaper
    thread while the player may be mid-tick. Not observed in four recoveries; no test covers it.
  - `R3-layout-fallback-out-of-bounds` (SUGGESTION): the zero-size fallback grid can go out of
    bounds for counts far above 16; unreachable through the parser (max 16).
- Slice T2 + T4 (`--base-ref e87a108`, 1694 lines, risk `medium`, `slice_budget_reached`):
  consent granted by the maintainer; lineage `review-b48c85520820f775`, one lens (reliability),
  **approved**, acknowledged, authority burned. Reviewed boundary is now `8db204d`. Advisory,
  non-blocking findings (not reopened; separate work only if the maintainer accepts it):
  - `R3-reply-drain-unbounded` (WARNING): `WaitForPipeDrain` has no timeout; a same-user client
    that writes and never reads hangs the single-instance server.
  - `R3-runloop-hot-spin` (WARNING): `RunLoop` retries with no backoff; a pipe-name collision
    (e.g. a second app instance, nMaxInstances 1) spins a core and floods diagnostics.
  - `R3-client-unmapped-failures` (WARNING): client exceptions other than cancellation (broken
    pipe, access denied) escape with undocumented exit codes; a zero-byte reply maps to 1, not 2.
  - `R3-acl-test-owner-masks-dacl` (WARNING): the ACL test would pass on the owner SID alone; it
    should assert the exact `D:` section.
  - `R3-oversized-reply-size` (SUGGESTION): oversized reply reports the buffer size, not the
    real size.
  - `R3-queue-ctor-unvalidated` (SUGGESTION): `AlertQueue` accepts capacity <= 0 and a negative
    max age.
- Slice T4b (`--base-ref 8db204d`, 565 lines, risk `medium`, `slice_budget_reached`): consent
  granted by the maintainer; lineage `review-c9bc26f67897ef30`, one lens (reliability),
  **approved**, acknowledged, authority burned. Reviewed boundary is now the T4b record commit.
  Advisory findings, all introduced by T4b itself -- all three fixed by T4c, see its task entry:
  - `R3-client-connect-timeout-message-regressed` (SUGGESTION, deterministic): a connect timeout
    (CosmicWin not running) now prints "did not reply in time" instead of "not running"; exit
    code still 2; no test asserts the stderr text. Fixed, commit `91f5783`.
  - `R3-drain-test-does-not-prove-timeout` (WARNING): the drain test disposes the rude client
    before the second one connects, so the old unbounded code would pass it too. Fixed, commit
    `fe10136`.
  - `R3-backoff-ceiling-exceeds-client-connect-timeout` (SUGGESTION): backoff caps at 5 s and
    resets only after a served connection; a client's ~1 s connect budget can miss a recovered
    server. Fixed, commit `94fae54`.
- Slice T6 (`545 lines`, risk `medium`): lineage `review-ba2d4e3013e17c30`, one lens
  (reliability), **approved**, acknowledged, authority burned. Advisory, non-blocking findings
  (not reopened; separate work only if the maintainer accepts it):
  - `R3-com-cleanup` (WARNING): review still flags COM cleanup around the overlay resource path;
    this was also caught by the independent verifier before native review and remediated by moving
    partial initialization ownership into fields so `ReleaseAllResources()` can clean failed setup.
  - `R3-failure-retry-loop` (WARNING): repeated draw/resource failure can retry every frame, with
    throttled logging and cache release. Current behavior matches T6 containment; T9 hardware run
    should check that no repeated failure appears in `alert-overlay.log`.
  - `R3-native-path-coverage` (WARNING): pure tests cover API/idle snapshot behavior only; real
    Direct2D/D3D draw, alpha fallback, rebuild, and GPU cost remain manual/hardware evidence for
    T9.
- Slice T7 (`459 lines`, risk `medium`): lineage `review-dd0ba74af27e9322`, one lens
  (reliability), **approved**, acknowledged, authority burned. Advisory, non-blocking findings
  (not reopened; separate work only if the maintainer accepts it):
  - `R3-custom-label-regression` (WARNING): the great-sage visual path uses theme titles rather
    than a tile's custom `Label`; this matches T7's warning/failed visual port, but T8 should not
    rely on custom labels unless intentionally restored.
  - `R3-public-constructor-abi` (WARNING): adding `StartedAt`/`Duration` to the public record
    constructor changes the constructor ABI. Source compatibility remains for existing named/default
    usage in this repo; external ABI stability is not promised for this feature branch.
  - `R3-small-tile-overdraw` (WARNING): small tiles may overdraw dense title/modules. T3 parser and
    layout cap count at 16; T9 visual comparison should include worst-case small tiles.
- Slice T8 (`381 lines`, risk `medium`): lineage `review-13be75f36453d741`, one lens
  (reliability), **approved after correction**, acknowledged, authority burned. Required correction:
  - `R3-alert-test-namespace` (BLOCKER, inferential): the new wiring test did not explicitly import
    `CosmicWin.App.Alerts`; added the import and targeted validation approved. The test had already
    compiled through existing references, but the explicit import removes ambiguity.
  Advisory, non-blocking findings (not reopened; separate work only if the maintainer accepts it):
  - `R3-alert-clock` (SUGGESTION): queue timestamps use `DateTimeOffset.UtcNow` directly in
    `AppComposition`; acceptable for T8 wiring, but a narrower injectable clock could make future
    timing tests less sleep-based.
  - `R3-disposal-chain` (WARNING): alert server disposal is chained with video wallpaper disposal;
    T9 should watch shutdown/restart behavior for any delayed pipe/server cleanup.
  - `R3-startup-cleanup` (WARNING): if server startup fails after partial construction, cleanup is
    best-effort through disposal. T9 should include disabled/enabled restart checks and log review.

## Next step

Hardware re-check of T10/T11 done by the parent, 2026-09-23 (app stopped, rebuilt, relaunched):

- T11 real-attach facts with `COSMICWIN_RUN_DESKTOP_TESTS=1`: 5 passed / 0 skipped. RED proven
  by the parent: with `Win32VideoWallpaperHost.cs` from `571c019` the new
  `...WholePrimaryMonitor...` fact failed (1/1), then the file was restored.
- T11 screenshot: the ultrawide video reaches the top and bottom edges, no black bars. A
  `warning:3 failed:3` alert keeps its tiles inside the work area, clear of the right-docked
  taskbar.
- T10: T9's covered-desktop probe repeated (`failed:2 duration:4` sent under a borderless
  topmost fullscreen form, which closes 5 s later): the tiles were held, then shown 1.5 s after
  uncover and cleared by 6 s. **Pass.**
- Observation, not fixed: the first alert right after relaunch did not appear while the
  desktop was visible. The likely cause is the NVIDIA overlay's "Press Alt+Z" toast, a caption-less
  full-monitor window that is foreground for a few seconds and matches the fullscreen rule, so the
  alert was held. Not reproduced afterwards. Consequence: at worst a held alert, never a lost one.

Next: native review of the T10/T11 commits, then slice 5 delivery (T7+T8+T9+T10+T11).
