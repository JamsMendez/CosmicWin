# Video wallpaper (v1, minimal scope)

## Objective

Let the user pick an MP4 file from the tray menu and have it play, looping forever, as the
desktop wallpaper (behind the desktop icons, in front of nothing — the real wallpaper).

## Why

User request, after the feasibility research (`docs/research/video-wallpaper-feasibility.md`)
and two working spikes (`spikes/AttachSpike`, `spikes/PlaybackSpike`, both uncommitted-turned-
committed as research evidence in 73f7bd3) confirmed the mechanism works on this machine, with
one hard requirement the original research missed: the host window must present through a
DXGI/D3D swapchain, never GDI (Progman carries `WS_EX_NOREDIRECTIONBITMAP` on this Windows
build; six independent GDI-painted configurations rendered zero pixels, a D3D swapchain worked
on the first try). See the research doc §3.6 for the full trail.

## Scope (v1 — explicitly minimal, user-selected 2026-09-21)

In scope:
- Primary monitor only.
- H.264 MP4 only.
- Copy the chosen file into `%LOCALAPPDATA%\CosmicWin\` (don't break if the user moves/deletes
  the original).
- One new tray menu entry ("Set video wallpaper…") opening a file picker.
- Persist the chosen (copied) path in the existing settings file.
- Loop forever (`SetLoop(true)`), muted.
- Re-attach after `TaskbarCreated` (Explorer restart) — the research doc flags this as the
  cheapest, most important recovery case to cover; nothing else from §3.5 (session lock, RDP,
  battery) is in scope for v1.

Explicitly out of scope for v1 (tracked as gaps in the research doc, not forgotten):
- Multi-monitor (one host per display, spanning, or duplicate).
- Pause-on-coverage / fullscreen-detection logic — the video keeps playing even if something
  covers it. Acceptable for v1: it's muted and behind everything else anyway.
- HEVC/VP9/AV1 or any codec beyond H.264 MP4.
- DPI-aware per-monitor placement beyond whatever the primary monitor's work area already gives.

## Constraints

- Native Win32 / .NET only. No third-party runtime dependency (CsWin32 is a source generator,
  already used throughout `CosmicWin.Interop` — consistent with existing dependency hygiene).
- `CosmicWin.Interop` is the only project allowed to touch Win32 (design D1/D8, existing rule).
- The host window must be `WS_CHILD` so the tiler's existing `IsTrackable` check excludes it with
  zero extra work (confirmed by the architecture mapping below — already pinned by
  `ChildWindowTrackabilityTests`).
- **Never fall back to GDI painting for the host window.** `IMFMediaEngine` must run in
  frame-server mode (own D3D11 device + DXGI swapchain, present via `TransferVideoFrame`), not
  legacy HWND mode — legacy HWND mode plausibly hits the same composition wall that killed six
  GDI attempts in the spike, and was never itself tested in production conditions.

## Architecture map (from delegated exploration, 2026-09-21 — see task log for full report)

- **Tray menu**: `CosmicWin.App/Tray/TrayMenuEntry.cs` (enum) → `TrayIconHost.MenuOrder`
  (`CosmicWin.App/Tray/TrayIconHost.cs:166`) → `TrayMenuController` (pure, delegate-injected,
  `CosmicWin.App/Tray/TrayMenuController.cs`) → wired in `TrayIconHost`'s constructor and in
  `CompositionRoot.BuildTrayMenuController` + `AppComposition.WireProduction`
  (`CosmicWin.App/AppComposition.cs:1160`). File-picker precedent: `TrayIconHost.PickBorderColor`
  (`CosmicWin.App/Tray/TrayIconHost.cs:192`) — same shape, `OpenFileDialog` instead of
  `ColorDialog`.
- **Settings**: `CosmicWin.App/Settings.cs` (pure record: `Default`, `Parse`, `Serialize`) +
  `CosmicWin.App/SettingsFile.cs` (disk I/O, `%LOCALAPPDATA%\CosmicWin\settings.conf`, never
  throws). Add `VideoWallpaperPath` as a fourth optional field, same pattern as `BorderColor`.
  Wired once in `AppComposition.WireProduction` (`persistX: value => SettingsFile.Save(stored =
  stored with { X = value })`).
- **New-HWND precedent**: none exists yet in `CosmicWin.Interop` — every existing Win32 wrapper
  (`Win32OverlayWindow`, `Win32WindowBorder`) is a static helper over an HWND it does *not* own.
  The actual precedent is the spike code: `spikes/AttachSpike/Program.cs` (window class
  registration, `WndProc`, Progman/WorkerW attach, D3D11/DXGI swapchain) and
  `spikes/PlaybackSpike/Program.cs` (`IMFMediaEngine` host, `Type.GetTypeFromCLSID` for the
  `MFMediaEngineClassFactory` coclass CsWin32 doesn't project). Both spikes' `NativeMethods.txt`
  list the exact new Win32/D3D11/DXGI/Media-Foundation symbols to merge into
  `CosmicWin.Interop/NativeMethods.txt` (currently 63 entries, none of them window-creation or
  D3D/MF related).
- **Tiler exclusion**: `Win32NativeWindowSource.IsTrackable` (`CosmicWin.Interop/Win32/
  Win32NativeWindowSource.cs:344`) short-circuits `false` the instant `isChild` is true, before
  ever reaching `WindowFilters.IsAutoExcluded`. A `WS_CHILD` host is excluded automatically.
- **App composition / lifecycle**: `AppComposition.WireProduction` (`AppComposition.cs:1121`) is
  the sole production factory; long-lived optional collaborators follow the nullable-field +
  `?.Dispose()`-in-`Dispose()` pattern already used for `_windowShown`/`_dialogAdapter`.
  `Dispose()` order is explicit and documented — the wallpaper host disposes in that same
  ordered block. No `TaskbarCreated` handling exists anywhere yet; it's new work, done inside the
  host's own `WndProc`.
- **Test seams**: `INativeDisplaySource` / `FakeNativeDisplaySource`
  (`CosmicWin.Interop/Win32/INativeDisplaySource.cs`, `CosmicWin.Interop.Tests/Win32/
  FakeNativeDisplaySource.cs`) is the shape to copy — one narrow interface, a real CsWin32-backed
  implementation, an in-memory fake for unit tests. Desktop-touching facts use
  `[RequiresDesktopFact]` / `COSMICWIN_RUN_DESKTOP_TESTS=1` +
  `COSMICWIN_DESKTOP_TEST_TERMINAL=<path>` (both required together, `CosmicWin.App` must be
  closed first, `docs/notes.md` has the exact commands).

## TDD mode

**Strict TDD: enabled** — source: user's global development instructions (no repo-level override
found). Red → Green → Refactor for every task below. Runner: `dotnet test
<Project>.Tests/<Project>.Tests.csproj` per project (not solution-wide — desktop tests within a
project serialize via `[Collection(RealDesktopCollection.Name)]`, but not across projects).

## Tasks

- [x] **T1 — Settings: `VideoWallpaperPath`.** Add the field to `Settings` (record, `Parse`,
  `Serialize`), round-trip tests in `SettingsTests.cs` / `SettingsFileTests.cs`. Route: delegated
  writer (2+ files). No Win32. **Done** — commit `aa2b3c7`. TDD: RED (compile failure on the new
  tests) → GREEN (732 passed, 0 failed, 6 pre-existing skips) → REFACTOR (diff reviewed, minimal,
  faithful to the `BorderColor`/`Tiling` shape). Spot-checked by re-running `dotnet test
  CosmicWin.App.Tests/CosmicWin.App.Tests.csproj` myself before committing.
- [x] **T2 — Merge native surface.** Add the new Win32/D3D11/DXGI/Media-Foundation entries from
  both spikes' `NativeMethods.txt` into `CosmicWin.Interop/NativeMethods.txt`; confirm the
  project still builds and CsWin32 generates clean bindings (no interop the spikes didn't already
  prove out). **Done** — commit `b44126e`, done directly (mechanical, no behavior/tests). Learned
  along the way: CsWin32's `NativeMethods.txt` does **not** support `#` comments (each line is
  resolved as a symbol name; a comment line produces a `PInvoke001` "not found" warning) — keep it
  plain, one symbol per line, no annotations. Deliberately excluded: `SetLayeredWindowAttributes`/
  `WS_EX_LAYERED`/`LWA_ALPHA` (proven unnecessary), all GDI paint symbols (`FillRect`,
  `BeginPaint`, `WM_PAINT`, ...), `MF_MEDIA_ENGINE_PLAYBACK_HWND` (legacy mode, out of scope), and
  `MFMediaEngineClassFactory` (not a resolvable symbol — the coclass is activated via
  `Type.GetTypeFromCLSID`, as the spike already does). Whole-solution build confirmed clean
  (`dotnet build CosmicWin.sln`), two pre-existing unrelated nullable warnings in
  `MultiMonitorWorkspaceAdapter.cs` untouched.
- [x] **T3 — Wallpaper host window.** Port the spike's window-class/WndProc/Progman-WorkerW-
  attach/D3D-swapchain sequence into `CosmicWin.Interop` behind a narrow seam interface (shape:
  `INativeDisplaySource`/`FakeNativeDisplaySource`), plus `TaskbarCreated` re-attach in the
  `WndProc`. Unit tests against the fake; a `[RequiresDesktopFact]` test for the real attach.
  Route: delegated writer — this is the highest-novelty piece (no existing new-HWND precedent in
  this codebase), brief the writer with the exact spike file paths. **Done** — commit `6ee4ae8`.
  Re-verified independently on real hardware after a session gap (fresh `dotnet build` +
  `COSMICWIN_RUN_DESKTOP_TESTS=1 dotnet test`, same 2/2 real-attach result) before committing.
  Implementation adds `IVideoWallpaperHost`, `Win32VideoWallpaperHost`, `DesktopLayoutDetector`,
  and focused interop tests. The host creates a real DXGI/D3D11 swapchain against the desktop-
  attached HWND and presents a black test pattern only; Media Foundation video playback remains T4.
  Review fixes included a hidden top-level `TaskbarCreated` receiver (the visible host becomes
  `WS_CHILD`, so it cannot be trusted to receive the broadcast), idempotent retry after partial
  attach/D3D failure, exact parent validation after `SetParent`, safe CsWin32 COM wrapper cleanup,
  class unregister on dispose, disposed-`WndProc` guarding, and cleanup if the initial `Present()`
  fails. Verification: `COSMICWIN_RUN_DESKTOP_TESTS=1 dotnet test CosmicWin.Interop.Tests/
  CosmicWin.Interop.Tests.csproj --filter "FullyQualifiedName~Win32VideoWallpaperHostRealAttachTests"`
  => 2 passed, 0 failed; `dotnet test CosmicWin.Interop.Tests/CosmicWin.Interop.Tests.csproj`
  => 163 passed, 28 skipped, 0 failed.
- [x] **T4 — Media Foundation frame-server playback.** Wrap `IMFMediaEngine` in **frame-server**
  mode (own D3D11 device + `TransferVideoFrame` into the host's swapchain) — genuinely new work,
  not a port: the spike's `PlaybackSpike` used legacy HWND mode for the loop-seam test only, and
  that mode is explicitly out per the constraints above. Route: delegated writer.

  **Done, after a real debugging detour worth recording.** First draft ticked the engine from a
  `WM_TIMER` on the same STA thread (`CoInitializeEx(COINIT_APARTMENTTHREADED)`) that owned a
  private pump window. That deadlocked **intermittently** on real hardware (found only by manual
  hardware verification — a temporary xunit fact pumping real Win32 messages, since production's
  WPF Dispatcher does this for free but a bare xunit test host does not; the automated test suite
  stayed green throughout because it never exercises a real video file). Two follow-up guesses
  (fetching `GetBackBuffer()` fresh per `DXGI_SWAP_EFFECT_FLIP_DISCARD` semantics, and setting
  `MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT`) were tried and reverted — neither was the real cause, and
  a "clean" 15-second run at one point turned out to be a timing coincidence, not a fix, when the
  identical code hung on a later run.

  A research pass confirmed the actual root cause: Media Foundation does not marshal STA objects
  to its internal MTA work-queue threads, and `IMFMediaEngineNotify` callbacks run on those MTA
  threads — an STA thread blocked synchronously inside an MF call at the wrong moment is a
  circular-wait COM deadlock (Microsoft's own "Media Foundation and COM" docs). Microsoft's own
  `meplayer.cpp` reference sample doesn't tick from the UI thread at all; it runs the whole
  per-frame loop on a dedicated worker thread. Fixed by matching that model exactly: engine
  creation, ticking, and teardown all run on one dedicated background `Thread` initialized with
  `CoInitializeEx(COINIT_MULTITHREADED)`, never a window or message loop. Also added
  `ID3D10Multithread::SetMultithreadProtected(true)` on the shared D3D11 device as defense-in-depth
  per Microsoft's D3D11-decoding guidance.

  **Verified twice, directly, on screen** — not inferred from a lack of exceptions: with
  `CosmicWin.App` closed and `COSMICWIN_RUN_DESKTOP_TESTS=1`, the manual harness attached the real
  host, played the real trimmed clip, and the actual video (not a black frame, not the earlier
  test pattern) was screenshotted rendering full-screen behind the desktop icons, twice in a row,
  full 15-second runs, zero hangs. The temporary manual-verification test file was deleted
  afterward per its own header comment. `dotnet test CosmicWin.Interop.Tests/
  CosmicWin.Interop.Tests.csproj` (full suite): 165 passed, 0 failed, 31 skipped, 196 total.
- [x] **T5 — Tray entry + file picker + settings wiring.** `TrayMenuEntry` value, `MenuOrder`,
  `TrayMenuController` delegate, `TrayIconHost` `OpenFileDialog` handler (mirror
  `PickBorderColor`), copy the picked file into `%LOCALAPPDATA%\CosmicWin\`, persist via T1's
  `VideoWallpaperPath`. Route: delegated writer. **Done** — App-layer only, no Win32/Interop
  touched. New `VideoWallpaperImport.Import` (fixed destination filename
  `video-wallpaper.<ext>`, overwrites on re-pick, lets `File.Copy`/`Directory.CreateDirectory`
  throw normally — unlike `SettingsFile.Save`, a failed video import needs to reach the user who
  just picked the file). `TrayMenuEntry.VideoWallpaper` placed between `BorderColor` and `Pause`
  (both are picker items, not mode switches). `AppComposition.Wire` gained a required
  `Func<string, string> importVideoWallpaper` seam (positioned before every optional parameter,
  since C# forbids a required parameter after one with a default) and optional
  `Action<string>? persistVideoWallpaperPath`; the `setVideoWallpaperPath` closure imports then
  persists, with an explicit comment marking where T6 adds the (re)start-playback call — no
  playback wiring in this task, by design. Spot-checked: `dotnet test
  CosmicWin.App.Tests/CosmicWin.App.Tests.csproj` => 742 passed, 0 failed, 6 skipped (same skips
  as T1's baseline, +10 new tests), confirmed myself before committing.
- [x] **T6 — AppComposition wiring.** Construct the T3/T4 host+playback service in
  `WireProduction` when `settings.VideoWallpaperPath is not null`; wire the T5 picker to
  (re)start playback on selection; dispose in the existing ordered `Dispose()` block. Route:
  delegated writer. **Done.** Found and solved a real problem the brief didn't anticipate:
  `IVideoWallpaperHost`/`IVideoWallpaperPlayer` were `internal` to `CosmicWin.Interop`, and
  `InternalsVisibleTo` only reaches the two test projects, not `CosmicWin.App` itself — moved both
  interfaces to the root `CosmicWin.Interop` namespace as `public` (matching the existing
  `IWindowShownWatcher`/`Win32WindowShownWatcher` precedent exactly), and made
  `Win32VideoWallpaperHost`/`MediaFoundationVideoWallpaperPlayer` `public sealed`. A second,
  finer problem surfaced from that: `Device`/`GetBackBuffer()` return CsWin32-generated D3D types
  that are themselves `internal` to `CosmicWin.Interop`, so a `public` interface couldn't expose
  them (CS0050/CS0053) without making the whole D3D surface public solution-wide. Fixed by marking
  just those two interface members `internal` (C# 11+ per-member interface accessibility) and
  implementing them via explicit interface implementation forwarding to ordinary internal members
  on the concrete class — keeps "only `CosmicWin.Interop` touches Win32" intact. `Wire(...)` gained
  `videoWallpaperHost`/`videoWallpaperPlayer`/`videoWallpaperPath` (all optional, mirroring
  `windowShown`); startup activation and the T5 tray hook both call `TryAttach()` then `TryPlay()`
  on the owning thread (`Win32VideoWallpaperHost` needs its window's messages pumped by the thread
  that created it, for `TaskbarCreated` re-attach). `Dispose()` stops the player before the host
  (its worker thread reads the host's D3D device on every tick). Verified myself: whole-solution
  build 0 errors, `CosmicWin.Interop.Tests` 165/0/31, `CosmicWin.App.Tests` 748/0/6 — both matching
  what was reported, no regressions.

## Delivery strategy

`ask-on-risk` (default) — triggered after T5 (running total ~2246 lines across the branch, well
past the ~400-line heuristic). User chose a single PR for the whole branch (research+spikes,
T1-T6) over a chained-PR split — open it once T6 closes.

## Progress log

- 2026-09-21: research doc updated with spike findings (commit 73f7bd3); this task file created;
  scope confirmed minimal (primary monitor, H.264 only, no pause-on-coverage) by explicit user
  choice; architecture mapped via delegated exploration (see conversation for full report).
