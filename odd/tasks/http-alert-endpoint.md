# HTTP alert endpoint

## Objective

Let third-party apps on this PC send `warning` / `failed` alerts over HTTP, next to the existing
named pipe that `CosmicWinAlert.exe` uses.

## Problem / why

Today alerts arrive only through the per-user named pipe (`NamedPipeAlertCommandServer`), so only a
terminal running `CosmicWinAlert.exe` can raise one. Tools such as CI watchers, scripts in other
languages, or editor plugins speak HTTP far more easily than a Windows named pipe.

## Decisions (maintainer, 2026-09-24)

- Callers run only on this PC. No LAN access.
- `POST http://127.0.0.1:<port>/v1/alerts`, JSON body `{ "warning": 2, "failed": 1, "duration": 5 }`,
  translated to the existing grammar (`warning:2 failed:1 duration:5`) and run through the SAME
  `HandleAlertCommand` handler (`Func<string,string>`) the pipe uses. The pipe stays unchanged.
- `Authorization: Bearer <token>`. Token auto-generated on first start into
  `%LOCALAPPDATA%\CosmicWin\alert-http.token`, compared in constant time.
- Settings: `alert-http = on|off` (default **off**), `alert-http-port` (default 47811). Port in use
  or listener failure -> traced, the pipe keeps working.
- `System.Net.HttpListener` (BCL, no new dependency).

## Scope

- `CosmicWin.Interop`: `AlertHttpProtocol` (pure: JSON -> command text, reply -> status code),
  `HttpAlertCommandServer : IAlertCommandServer`, token store.
- `CosmicWin.App`: `Settings` keys, `AppComposition` wiring of a second server with the same handler.
- `README`: how to call it (curl / PowerShell example), where the token lives.

Out of scope: LAN access, TLS, other verbs or routes, changing the pipe or the alert grammar.

## Constraints

- Local only: no push, no gh (maintainer does all GitHub work).
- Security (defense in depth, all required):
  - bind the loopback prefix only, AND refuse any request whose `RemoteEndPoint` is not loopback;
  - refuse a request carrying an `Origin` header (browsers), and a `Host` other than
    `127.0.0.1:<port>` / `localhost:<port>` (DNS rebinding);
  - require `Content-Type: application/json` and the bearer header -- together they force a CORS
    preflight, and `OPTIONS` is never answered with CORS headers;
  - 1 KB body cap, request read timeout, one request at a time is fine (same as the pipe).
- Status mapping: 202 ok, 400 bad JSON / parser error, 401 bad or missing token, 403 origin/host/
  non-loopback, 404 other path, 405 other method, 413 body too large, 415 wrong content type,
  429 `queue full`, 503 `alerts are disabled`. Body: the handler's reply text (`ok` / `error: ...`).
- Resolved 2026-09-24 (H2 measurement): run under `runas /trustlevel:0x20000` (basic user, `admin=False`),
  `HttpListener.Start()` succeeded for BOTH `http://127.0.0.1:47899/` and `http://localhost:47899/`.
  Chosen (updated in H2b): BOTH `http://127.0.0.1:<port>/` and `http://localhost:<port>/` (this
  machine resolves `localhost` to `::1`), falling back to `127.0.0.1` alone if the pair fails; no
  urlacl. Loopback connections with any `Host` reach our gate; with two prefixes http.sys itself
  answers a LAN-address connection with 400 Invalid Hostname. The loopback `RemoteEndPoint` check
  stays mandatory either way (the fallback single-prefix setup does not filter LAN connections).

## TDD mode

**Strict TDD: enabled** -- source: user's global instructions. Runner:
`dotnet test <Project>.Tests/<Project>.Tests.csproj` (full: `dotnet test CosmicWin.sln`).
RED observed before every production change; GREEN; then REFACTOR.

## Delivery

Strategy: `ask-on-risk` (default). Forecast: ~600-800 authored lines over H1-H5 (> 400).
Chain strategy chosen by the maintainer on 2026-09-24: **`feature-branch-chain`** (slice branches
merge into `feat/http-alert-endpoint`, which merges to local `main` at the end). RDD: on (global).
Reviewed boundary: branch point `a3e3ba8`.

## Tasks

- [x] H1 -- `AlertHttpProtocol` (pure, Interop): JSON body -> command text, fields in the order
  written (`warning` / `failed` / `duration`, exact lowercase, JSON int32); unknown field, non-int
  value, non-object, invalid JSON -> short error. Repeated fields and all numeric limits are passed
  through for `AlertCommandParser` to reject, so both transports reject exactly the same commands.
  Reply -> status: `ok` 202, `queue full` 429, `alerts are disabled` 503, `internal error` 500,
  other `error:` 400, unrecognised 500. Constants `AlertsPath`, `MaxBodyBytes` 1024, `DefaultPort`
  47811. Route: inline (one new file + its tests). Commit `4bad3d3` (2 files, +204).
  - Strict TDD: RED 29/29 failed against a `NotImplementedException` stub; GREEN 29/29.
  - Checks: build 0 errors (3 pre-existing warnings in untouched files); Interop suite 270 passed,
    40 skipped, 0 failed.
  - Review: assess vs `a3e3ba8` = medium, `under_budget` (308 lines) -> pending in the slice.
- [x] H2 -- `HttpAlertCommandServer : IAlertCommandServer`: listener lifecycle (`Start` idempotent,
  never throws, `Dispose` stops), request gate in the order of the constraints above, handler call,
  handler exceptions -> 500 `error: internal error`. Integration tests over a real loopback port
  (`HttpClient`), plus the elevation measurement. Route: delegated writer (writer trigger: server +
  integration tests). Commit `3eef308` (2 files, +921).
  - Strict TDD (writer): RED 27/27 failed against a `NotImplementedException` stub (e.g.
    `Constructor_PortTooLarge_Throws`: no exception thrown); GREEN 27/27, re-run stable.
  - Checks: writer build 0 errors (3 pre-existing warnings); Interop 297 passed / 40 skipped /
    0 failed. Parent spot check: same 297/40/0.
  - Review: assess vs `a3e3ba8` = medium, `slice_budget_reached` (1236 lines, H1+H2) -> due. Consent
    granted by the maintainer. Lineage `review-d774e6b4251162d2`, one lens (reliability): APPROVED,
    acknowledged, authority burned. Reviewed boundary advances to `3eef308`.
    Advisory findings (non-blocking), all accepted into H2b: R3-001/002 Host gate may be shadowed by
    http.sys prefix routing (localhost:port possibly 400, host-403 test may not prove the app gate);
    R3-003 non-loopback 403 not deterministically tested; R3-004 `Start` after `Dispose` leaks a
    listener; R3-005 no backoff when `GetContext` keeps failing; R3-006 `Bearer` scheme matched
    case-sensitively; R3-007 no exact-size body boundary test, chunked test may not be chunked.
- [x] H2b -- Close the H2 review findings above. Route: delegated writer (same writer, context
  reuse) + one inline test fix. Commits `3895628` (2 files, ~+500) and `94a962d` (test only).
  - R3-001/002 measured with raw `TcpClient` requests against the single `127.0.0.1` prefix:
    `Host: localhost:<port>` and `127.0.0.1:<port>` reached the app (202); `Host: evil.example:1234`
    reached the app and got OUR 403. So the reviewer's "http.sys answers 400 first" was wrong for
    that setup. But a real `http://localhost:<port>/` URL TIMED OUT: this machine resolves
    `localhost` to `::1`, which the IPv4-literal prefix never listens on. Fix: register
    `http://localhost:<port>/` too (fallback to `127.0.0.1` only if that bind fails). With two
    prefixes, http.sys itself rejects a LAN-address connection with 400 Invalid Hostname; loopback
    connections with any Host still reach our gate. Tests `RawHost_*`,
    `LiteralLocalhostUrl_ViaRealDnsResolution_ReachesTheServer`; LAN fact accepts any 4xx or refusal.
  - R3-003: pure `IsLoopbackRemote(IPEndPoint?)`, unit-tested (RED: CS0117 compile failure).
  - R3-004: `Start` after `Dispose` is a no-op. The writer's test started the server first, so the
    `_thread` guard hid the missing check: a parent mutation (drop `_disposed`) failed NO test.
    Fixed inline in `94a962d` (never-started -> Dispose -> Start); mutation now fails it
    (`Assert.Null() Failure`), restored code passes.
  - R3-005: bounded 100 -> 500 ms backoff, mirroring the pipe server. NOT unit-tested: a repeating,
    non-shutdown `GetContext` failure cannot be produced from a black-box test (documented gap).
  - R3-006: `Bearer` case-insensitive. The writer did not observe RED; parent mutation (back to
    `Ordinal`) failed `BearerSchemeLowercase_IsAccepted`, restored code passes.
  - R3-007: exact 1024-byte body accepted, 1025 -> 413, raw chunked over the cap -> 413.
  - Honest TDD note: for R3-004..007 the writer batched the fixes and did not observe RED per item;
    RED was recovered by parent mutation for R3-004 and R3-006 only.
  - Checks: build 0 errors (3 pre-existing warnings); Interop 314 passed / 40 skipped / 0 failed
    (writer, and parent after `94a962d`).
  - Review: assess vs `3eef308` = medium, `slice_budget_reached` (553 lines) -> due. Consent granted
    by the maintainer. Lineage `review-f2885fa52f1d8c8e`, one lens (reliability): APPROVED,
    acknowledged, authority burned. Reviewed boundary advances to the H2b doc commit.
    Advisory findings, all taken inline (route: inline, one file already understood):
    R3-101 `Dispose` no longer disposes `_stopping` when the bounded join timed out (RunLoop could
    still read its token); R3-102 a failed listener attempt is closed before the fallback;
    R3-103 the constraint above now states the two-prefix choice. R3-101/102 are NOT unit-tested:
    a join timeout and a lingering failed listener cannot be observed from a black-box test.
    Checks after the fixes: build 0 errors; Interop 314 / 40 / 0.
- [x] H3 -- Token store (create-once, 32 random bytes base64url, file readable by the user only,
  reuse on restart, constant-time compare) + `Settings` keys `alert-http` / `alert-http-port` with
  defaults and round-trip. Route: delegated writer (writer trigger: 2 non-trivial files).
  Commit `033255d` (4 files, +575/-2): `CosmicWin.App/Alerts/AlertHttpTokenFile.cs` (no custom ACL:
  `%LOCALAPPDATA%` inherits a per-user ACL; atomic temp-file + `File.Replace`/`File.Move`),
  `Settings.cs` `AlertHttpEnabled` (default off) / `AlertHttpPort` (default 47811, 1..65535).
  - Strict TDD (writer): token file RED 14/14 `NotImplementedException` against a stub; settings RED
    = 15 compile errors (CS1061/CS1739, members absent); GREEN 14/14 and 107/107.
  - Checks: build 0 errors (3 pre-existing warnings); App 943 passed / 6 skipped / 0 failed
    (writer and parent spot check).
  - Review: assess vs `4ecd665` = medium, `slice_budget_reached` (577 lines) -> due. Consent granted
    by the maintainer. Lineage `review-c7c037a7fe9ab126`, one lens (reliability): APPROVED,
    acknowledged, authority burned. Reviewed boundary advances to `033255d`.
    Advisory findings, all accepted into H3b: first-run creation race (WARNING); catch filters too
    narrow for "never throws" (WARNING); token-leak test is vacuous (SUGGESTION); Replace-branch
    persistence untested (SUGGESTION).
- [x] H3b -- Close the H3 review findings. Route: delegated writer (same writer, context reuse).
  Commit `7ff2c97` (2 files, +214/-17).
  - Race: re-read the destination before the destructive move/replace and again if it loses the
    race, adopting the winner's token. RED: 8-thread first run, 7/8 callers got null. GREEN, 3x.
  - Never throws: `IsRecoverable` filter (all but OOM/StackOverflow/AccessViolation, mirroring
    `AppComposition.IsRecoverableAlertLayerFailure`). RED: empty path threw `ArgumentException`
    under the old filter. The colon-in-segment case already surfaced as `IOException` here, so its
    test is coverage, not a fix (stated in the test).
  - Token-leak test: a malformed-file replacement now emits a diagnostic; new test asserts the new
    token value is absent from it (RED by removing the diagnostic: empty collection).
  - Replace persistence: second call after replacement returns the same token; no defect existed,
    RED recovered by mutation (skip `File.Replace`).
  - Checks: build 0 errors (3 pre-existing warnings); App 948 passed / 6 skipped / 0 failed (writer
    and parent spot check).
- [x] H4 -- `AppComposition` wiring: when `alerts-enabled` AND `alert-http` are on, start the HTTP
  server with `HandleAlertCommand`; start failure is traced and the pipe still runs; disposed with
  the app. Wiring tests with a fake server factory. Route: delegated writer (writer trigger:
  `AppComposition.cs` + a new wiring test file). Commit `a8ca0c1` (2 files, +262).
  - `Wire` gains `alertHttpEnabled`, `alertHttpPort`, `createHttpAlertCommandServer`,
    `loadAlertHttpToken`; the HTTP block runs after the pipe started, inside `alertsEnabled`, wrapped
    in `IsRecoverableAlertLayerFailure`; token loaded only when starting; production passes
    `settings.AlertHttpEnabled` / `AlertHttpPort`. Trace `alert-http listening port=<port>` is written
    after `Start()` even if the listener then reports a bind failure through its own diagnostic.
  - TDD: the writer observed RED only as one compile error (CS1739, new `Wire` parameter absent),
    not per behavior. Parent recovered per-behavior RED by mutation, each restored afterwards:
    gate forced on -> `HttpDisabled_FactoryNeverCalledAndTokenNeverLoaded` failed; catch disabled ->
    both `...Throws_PipeStillStarted...` failed; dispose removed -> `BothServersAreDisposedOnShutdown`
    failed; null-token branch skipped -> `NullToken_...` failed.
  - Checks: build 0 errors (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 190,
    Alert 13, Interop 314 (+40 skipped), App 955 (+6 skipped), 0 failed (writer and parent).
- [x] H4 review: assess vs `033255d` (H3b+H4) = medium, `slice_budget_reached` (541 lines) -> due.
  Consent granted by the maintainer. Lineage `review-ce5067b3ed04237d`, one lens (reliability):
  APPROVED, acknowledged, authority burned. Reviewed boundary advances to `22e9205`. Advisory
  findings, all accepted into H4b: the token Replace branch could still silently overwrite a token
  another caller had just returned (WARNING, true: the H3b re-read only narrowed it); race test used
  `Parallel.For` + `Barrier` (WARNING); start-failure trace unasserted (SUGGESTION).
- [x] H4b -- Close the H4 review findings. Route: delegated writer (H3 writer, context reuse).
  Commit `2298692` (3 files, +183/-95).
  - Token creation serialized by a named mutex `Local\CosmicWin.AlertHttpToken` (5 s timeout ->
    diagnostic + null; abandoned = acquired); read-validate-create runs inside it; the re-check
    machinery was removed. RED against the H3b code: the new malformed-file race test failed 3/3
    runs (different tokens, none on disk); the first-run race test failed 1/3 once on dedicated
    threads (`Parallel.For` had masked it). GREEN 5/5 repeated runs.
  - Race tests now use 8 dedicated threads with bounded barrier and join timeouts.
  - Wiring tests assert `alert-http-start-failed` present and `alert-http listening` absent; the
    vacuous `Assert.Null(h.HttpServer)` removed. RED by mutating the trace line.
  - Checks: build 0 errors (3 pre-existing warnings); `dotnet test CosmicWin.sln` Layout 190,
    Alert 13, Interop 314 (+40 skipped), App 956 (+6 skipped), 0 failed (writer and parent).
- [x] H5b -- Misleading start trace. Route: delegated writer, then inline revert.
  - What was claimed: the parent read the port-in-use hardware run as "`Start()` succeeds silently
    under a Winsock squatter" and had a writer add a post-start self-probe (`d20f6fb`, +185 server,
    +130 tests, a probe header).
  - What was true: the writer could not reproduce it (`Start()` always threw), and the trace proved
    the writer right: the server HAD reported `alert http: failed to start listening on port 47811
    ... HttpListenerException` twice. The parent's filter matched only `alert-http` and missed them.
    The one real defect: `AppComposition` traced `alert-http listening` regardless of the outcome.
  - Fix: `f28dbd3` reverts the self-probe and keeps only the wording `alert-http start requested
    port=<port>` (+ wiring tests); the server's own failure line carries the outcome. Covered by
    `Start_PortAlreadyInUse_ReportsDiagnosticAndDoesNotThrow` and the 3 wiring assertions.
  - Checks: build 0 errors (3 pre-existing warnings); Layout 190, Alert 13, Interop 314 (+40
    skipped), App 956 (+6 skipped), 0 failed.
- [x] H5 -- README section (curl + `Invoke-RestMethod` examples, token path, settings) and hardware
  check: warning and failed via HTTP, 401 without token, a browser `fetch` from a page blocked, port
  in use -> pipe still works. Route: inline.
  - README: new "Alerts" section (pipe CLI, HTTP endpoint, settings, token, curl +
    `Invoke-RestMethod`, status table). Uncommitted until H5b lands (it claims the port-in-use trace).
  - Hardware run 2026-09-25 (UTC), shell elevated, build of `258ecc9` + H5 branch via
    `scripts/run.ps1`; `settings.conf` backed up to the session scratchpad, `alert-http = on` added.
    - [x] start: `alert-http listening port=47811`; token file created (43 bytes); token value
      never appears in `desktop-trace.log`.
    - [x] real curl matrix: `warning` via 127.0.0.1 -> 202 `ok`; `failed` via `localhost` -> 202;
      no token / wrong token -> 401; `Origin` -> 403; `OPTIONS` preflight with `Origin` -> 403, no
      `Access-Control-*` headers; bad JSON -> 400; `warning:99` -> 400 (parser message);
      `text/plain` -> 415; GET -> 405. Every rejection traced as `alert-http rejected <status>`.
      Browser check was done with curl sending `Origin` + a preflight, not a real browser page.
    - [x] display: the accepted HTTP alerts were HELD while the foreground window was
      `CosmicWin Video Wallpaper` (a pipe alert was held too). A probe form brought to the
      foreground released them in FIFO order: warning (HTTP) 56:23.55, failed (HTTP via localhost)
      56:26.83, warning (pipe) 56:30.10, each 3 s. NOT an HTTP issue: pre-existing, see follow-ups.
    - [x] port in use: a Winsock `TcpListener` on 127.0.0.1:47811 before start. First run: the
      server traced both failed attempts, but `AppComposition` also traced `listening` (-> H5b).
      Requests timed out against the squatter; after it exited, connection refused (no listener, as
      expected: no retry by design). Pipe alert shown (57:45.70). Re-run with `f28dbd3`
      (06:17:24): both `failed to start listening` lines, then `alert-http start requested
      port=47811`; pipe alert shown 06:17:33.66, done 35.67.
    - Cleanup: app stopped (it was not running before the run), `settings.conf` restored from the
      backup (no `alert-http` keys). `alert-http.token` left in `%LOCALAPPDATA%\CosmicWin` (reused
      if the endpoint is turned on; delete to rotate).

- [x] Final slice review: assess vs `22e9205` (H4b, H5b + revert, README) = medium,
  `slice_budget_reached` (414 lines) -> due. Consent granted by the maintainer. Lineage
  `review-0bfef30a5ed7bab0`, one lens (reliability): APPROVED, acknowledged, authority burned.
  Reviewed boundary advances to `ba0bb62`. Advisory findings, both taken inline (route: inline, one
  understood file + its tests):
  - R3-mutex-ctor-outside-never-throws (WARNING): the named `Mutex` constructor ran outside the
    never-throw contract. Now caught with `IsRecoverable` -> diagnostic + null. RED observed for
    real: `WaitHandleCannotBeOpenedException` escaped when an `EventWaitHandle` owned the name.
  - R3-lock-timeout-path-unproved (SUGGESTION): new test holds a private lock past a 100 ms timeout
    -> null, one `timed out` diagnostic, no file. The branch already worked, so RED came from a
    mutation (timeout branch proceeds anyway -> `Assert.Null() Failure`), then restored. Enabled by
    an internal `LoadOrCreate(path, onDiagnostic, lockName, lockTimeout)` overload.
  - Checks: build 0 errors (3 pre-existing warnings); Layout 190, Alert 13, Interop 314 (+40
    skipped), App 958 (+6 skipped), 0 failed.

## Follow-ups (outside this feature)

- The video wallpaper host window can be the foreground window right after start, and the covered-
  desktop check then holds every alert until another window takes the foreground. Seen on
  2026-09-25 with both the pipe and HTTP. Not investigated; for the maintainer to prioritise.

## Acceptance criteria

- With `alert-http = on`, `curl -X POST -H "Authorization: Bearer $t" -H "Content-Type: application/json"
  -d '{"warning":1}' http://127.0.0.1:47811/v1/alerts` shows a warning alert and returns 202 `ok`.
- With the default settings no port is opened.
- Every rejection path in the status mapping has a test.
- `dotnet build CosmicWin.sln` 0 errors; `dotnet test CosmicWin.sln` 0 failures.

## Progress

- 2026-09-24: branch `feat/http-alert-endpoint` from `a3e3ba8`; defaults confirmed by the maintainer;
  explored `HandleAlertCommand` (`AppComposition.cs:402`), server wiring (`AppComposition.cs:902`),
  `AlertPipeProtocol` replies, `Settings` key convention (`alerts-enabled`).
- Engram mirror `odd/http-alert-endpoint/tasks`: PENDING (mem_save refused: multiple active runtime
  sessions match the project).

## Next step

All tasks done. Review the last slice if due, merge `feat/http-alert-endpoint-h5` into
`feat/http-alert-endpoint`, then ask the maintainer about merging the feature into local `main`
(nothing is pushed).
