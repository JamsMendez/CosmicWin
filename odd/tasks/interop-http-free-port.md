# Interop HTTP tests: retry on a lost free port

## Objective

`CosmicWin.Interop.Tests/LocalHttpCommandServerTests.cs` binds a real `LocalHttpCommandServer` on a
port from `GetFreePort()`, which releases the port before the server binds it. Another process can
take it in between; `Start()` never throws, it reports `alert http: failed to start listening on port
N (127.0.0.1)` and stays inert, so the test fails later with a bare connection refusal.

## Why

Same release-then-bind gap closed in the App end-to-end tests by `WireOnFreePort`
(review finding R3-free-port-toctou, `CosmicWin.App.Tests/Alerts/AlertHttpEndToEndTests.cs`). This
was the known leftover in the Interop suite. Decided by the maintainer 2026-10-01: do the refactor.

## Scope

- Test code only, one file: `CosmicWin.Interop.Tests/LocalHttpCommandServerTests.cs`.
- No production change.
- Tests that deliberately share a port (port-already-in-use) or never start a server keep their
  explicit port.

## Constraints

- TDD: strict, enabled (global CLAUDE.md). Runner:
  `dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj`.
- Retry signal is the final bind-failure diagnostic, mirroring `WireOnFreePort`; at most 5 attempts,
  then fail with the collected diagnostics.

## Tasks

- [x] T1 -- `StartOnFreePort` helper + proof test (first port held by a `TcpListener` -> server lands
  on another port and answers 202); route every started-server test through it.
  Route: delegated direct (one writer; ~65 call sites, preparation reading).

## Acceptance

- Proof test fails before the helper exists (RED), passes after.
- Full Interop suite green, same pass/skip counts as on main plus the new test.

## Progress

- 2026-10-01: branch `test/interop-http-free-port` created off main 8621f32.
- 2026-10-01: T1 done (delegated writer). RED: no-retry stub -> proof test `Assert.NotEqual` failed
  (same port). GREEN: Interop 492 passed / 42 skipped / 0 failed (baseline 491/42/0), re-run by the
  parent. Build: same 6 pre-existing warnings, none in this file. Left on explicit ports: constructor
  facts, Dispose_BeforeStart, Start_AfterDispose (never start), and the second server of
  Start_PortAlreadyInUse (must reuse the occupied port).
- 2026-10-01: T1 commit 77b65be. Assessed medium, `slice_budget_reached`; consent granted by the
  maintainer; review-9dada7fbe66534df (one lens, reliability) approved and acknowledged. Reviewed
  boundary advances to 77b65be.

## Follow-ups (non-blocking SUGGESTIONs from review-9dada7fbe66534df)

- R3-exhaustion-path-unproved: no test covers every attempt losing its bind (throws with the
  diagnostics, each loser disposed).
- R3-sink-forwards-to-test-output-off-thread: the shared sink now forwards every diagnostic to
  `ITestOutputHelper.WriteLine`, possibly from the server's background loop after the test ended;
  guard it with try/catch or record before forwarding.
