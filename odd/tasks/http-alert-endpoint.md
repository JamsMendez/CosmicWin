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
- [ ] H3 -- Token store (create-once, 32 random bytes base64url, file readable by the user only,
  reuse on restart, constant-time compare) + `Settings` keys `alert-http` / `alert-http-port` with
  defaults and round-trip. Route: delegated writer (2 non-trivial files).
- [ ] H4 -- `AppComposition` wiring: when `alerts-enabled` AND `alert-http` are on, start the HTTP
  server with `HandleAlertCommand`; start failure is traced and the pipe still runs; disposed with
  the app. Wiring tests with a fake server factory. Route: inline or delegated per size.
- [ ] H5 -- README section (curl + `Invoke-RestMethod` examples, token path, settings) and hardware
  check: warning and failed via HTTP, 401 without token, a browser `fetch` from a page blocked, port
  in use -> pipe still works. Route: inline.

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

H2 (`HttpAlertCommandServer`) on slice branch `feat/http-alert-endpoint-h2`, starting with the
elevation measurement of the `127.0.0.1` vs `localhost` prefix.
