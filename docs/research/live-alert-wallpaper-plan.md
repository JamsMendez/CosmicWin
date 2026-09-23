# Live alert wallpaper — feasibility and plan

**Status:** analysis and plan only, 2026-09-23. Nothing implemented. Route decided on 2026-09-23:
**native Direct2D / DirectWrite** (§3). Next step is the T0 spike.

**Idea:** show live alert tiles on the wallpaper. An external command such as
`warning:2 failed:1` shows N alert tiles (warning or failed layer each) arranged on the display for
a few seconds, then the desktop returns to the normal video.
**Visual reference:** the great-sage page (the HTML/JS/CSS the current wallpaper video was
recorded from), `great-sage/backgroud-processing/` (`index.html`, `styles.css`, `script.js`,
~157 KB) in the maintainer's WSL home. Its alert layer is the design reference; its code is not
reused.
**Target OS checked:** Windows 11 build 10.0.26200 (25H2).

Evidence labels: **[code]** read in a repository, **[measured]** sampled on this machine,
**[inferred]** reasoning, not verified.

---

## 1. Verdict

- **A permanent live-page wallpaper is not recommended.** It costs ~28 % of the GPU 3D engine
  continuously **[measured]**, about 4–5× the current video wallpaper.
- **Chosen route:** keep the Media Foundation video as the wallpaper and draw the alert layer
  natively with Direct2D / DirectWrite onto the same swapchain back buffer, only while an alert is
  on screen. No new dependency, no browser engine, no second window.

## 2. Measurements

Method: per-process sum of the `\GPU Engine(*engtype_3D)\Utilization Percentage` performance
counter, grouped by PID, three 2 s samples. Task Manager on this machine does not offer a
per-process GPU column, and the Performance tab total is the busiest engine, not a sum, so
subtracting totals is not valid. Display resolution and refresh rate were not recorded for these
samples; record them in T0 and T9.

| Scenario | GPU 3D | Memory |
|---|---|---|
| CosmicWin video wallpaper, visible | ~6 % **[measured]** | — |
| CosmicWin video wallpaper, covered by a fullscreen window | 0.5–0.8 % **[measured]** | — |
| Page in Edge, fullscreen, only tab | 27–28 % **[measured]** | 400–500 MB for the tab **[measured]** |
| Page in Chrome, fullscreen | 55–57 % **[measured]** | — |

The Chrome figure is the whole Chrome GPU process; whether other tabs contributed was not
verified. CPU for the page was 0.2–5 % **[measured]**: Chromium moves the 2D canvas work to the GPU.

Why the page is expensive **[code]**, `script.js`:

- `render()` (~line 3119) redraws the whole scene every `requestAnimationFrame`, at monitor refresh
  rate.
- The main canvas uses DPR up to 2 (~line 940), i.e. native resolution on HiDPI displays.
- A second WebGL canvas draws the nebula (~line 138).
- 1280 stars, 750 particles, `shadowBlur` glows (~line 1075), per-grain film grain.
- The error layer copies and pixelates the full canvas every frame (`drawFailureLayer`, ~line 3052).
- The overlay state is a single slot (`failureState` / `failureKind`, ~line 231), so N tiles need
  per-tile state.

The native route draws only the alert layer; the scene behind it is the already-decoded video.

## 3. Decision: native Direct2D (decided 2026-09-23)

`docs/research/video-wallpaper-feasibility.md` states: *"native Windows / .NET only, no
third-party dependencies"*.

- **Rejected — WebView2:** needs the `Microsoft.Web.WebView2` NuGet package at runtime, costs
  ~28 % GPU and 400–500 MB while shown, and the page would still need a rewrite for per-tile state.
- **Chosen — Direct2D / DirectWrite:** part of Windows, reachable through CsWin32 like the rest of
  the interop. The alert design (bands, title, modules) is ported to C#; the HTML is only a visual
  reference.

Why it fits the existing code **[code]**:

- The device is created with `D3D11_CREATE_DEVICE_BGRA_SUPPORT` and the swapchain uses
  `DXGI_FORMAT_B8G8R8A8_UNORM` (`Win32VideoWallpaperHost.cs` ~lines 417 and 446). Both are what
  Direct2D interop with a DXGI surface requires.
- Every frame goes through `MediaFoundationVideoWallpaperPlayer.Tick()`:
  `TransferVideoFrame(backBuffer, …)` then `host.Present()` (~lines 444–445). An overlay drawn
  between those two calls lands on top of the video in the same present. No second window, no
  z-order fight with the 400 ms re-attach.
- `Tick()` runs on the player's own thread and already swallows failures, so a failing overlay
  cannot crash the pump.
- The D3D11 device can be taken from the back buffer (`ID3D11DeviceChild::GetDevice`), so
  `IVideoWallpaperHost` does not need a new member **[inferred]**.

## 4. Design

```
CLI / WSL ──► CosmicWin.exe --alert "warning:2 failed:1"
                     │  named pipe (current user only)
                     ▼
              AlertCommandParser ─► AlertQueue ─► AlertTileLayout
                                                     │ tile rects + kinds + start time
                                                     ▼
   player thread:  TransferVideoFrame ─► IFrameOverlay.Draw(backBuffer, now) ─► Present
                                         (Direct2DAlertOverlay; no-op when idle)
```

- **Named pipe, not an HTTP webhook.** A localhost HTTP listener can be reached by any web page
  open in a browser (cross-origin `fetch`). A pipe with a current-user ACL opens no port. WSL can
  still call it through interop: `/mnt/c/.../CosmicWin.exe --alert ...`.
- **`--alert` is a client, never a second app.** It is handled next to `TryHandleTaskCommand`,
  before WPF starts, sends one message and exits. No instance running or connect timeout: exit
  code non-zero with a one-line message on stderr.
- **Pipe limits:** max message size, read timeout, one client at a time. Malformed or oversized
  input is rejected and logged, never partially applied.
- **Tiles are computed in C#** as pure, testable functions; the overlay only draws what it receives.
- **`IFrameOverlay` seam in the player:** called between `TransferVideoFrame` and `Present`. The
  default is a no-op, so idle cost is zero. Direct2D resources (factory, device context, target
  bitmap, DirectWrite formats) are created lazily on the player thread and rebuilt when the D3D
  device changes (swapchain re-created after an Explorer restart).
- **Flip model:** with a flip-model swapchain, buffer 0 is always the current back buffer, so one
  target bitmap over it should stay valid across presents **[inferred]**; T0 confirms it.
- **v1 is primary-monitor only**: the current host window covers only the primary work area
  (`Win32VideoWallpaperHost.cs`, `CreateHostWindow` / `GetPrimaryWorkArea` ~line 607) **[code]**.

Relevant existing code **[code]**:

- `IVideoWallpaperHost` / `Win32VideoWallpaperHost` (CosmicWin.Interop): Progman/WorkerW attach,
  D3D11 flip-model swapchain.
- `MediaFoundationVideoWallpaperPlayer` (CosmicWin.Interop): `IMFMediaEngine` frame-server mode on
  its own MTA thread; `Tick()` is the per-frame hook.
- `AppComposition.WireProduction`: builds host and player; the watch tick (`WatchInterval`, 400 ms,
  `AppComposition.cs` ~line 110) re-runs `TryAttach()` while the video is active.
- No inbound IPC exists today. `AppComposition.TryHandleTaskCommand` only handles
  `--install-task` / `--uninstall-task` in a fresh process, and `AppEntryPointThinnessTests`
  pins it as the only command entry in `App.xaml.cs`.
- `NativeMethods.txt` (CosmicWin.Interop) lists the CsWin32 APIs; the Direct2D / DirectWrite
  entry points (`D2D1CreateFactory`, `DWriteCreateFactory`) and interfaces must be added.
- Settings live in plain-text `%LOCALAPPDATA%\CosmicWin\settings.conf` (`Settings.cs`).
- Tests: xUnit with hand-written fakes (for example `FakeVideoWallpaperHost`,
  `VideoWallpaperWiringTests`).

## 5. Open question

**An alert arrives while the wallpaper is covered by a fullscreen window** (the case fixed in
a023fac). The tiles would be drawn but nobody sees them. Options: drop it with a log line, or keep
it queued with a max age and show it when the desktop is visible again. To decide before T2.

## 6. Tasks

One work-unit commit per task. TDD (RED → GREEN → REFACTOR) on the pure parts.

| # | Task | Where | Check |
|---|---|---|---|
| **T0** | **Throwaway spike:** in the existing `Tick()`, draw a rectangle and DirectWrite text over the video with Direct2D on the back buffer. Does it show under the icons? Does it survive the 400 ms re-attach and an Explorer restart (device re-created)? Measure GPU with and without the overlay, record resolution and refresh rate | `spikes/` or throwaway branch | Manual, on hardware |
| T1 | `AlertCommandParser`: `kind:count` grammar, kinds `warning` / `failed`, bounds, optional duration | App | xUnit |
| T2 | `AlertQueue`: bounded FIFO, one alert at a time, injected clock, behavior when covered (§5) | App | xUnit |
| T3 | `AlertTileLayout`: N → grid by display aspect ratio, in command order | App or Layout | xUnit |
| T4 | Named pipe server (current-user ACL, size limit, read timeout, one client) and `--alert` client handled before WPF next to `TryHandleTaskCommand`; non-zero exit when no instance | Interop + App | Integration: round trip, malformed, oversized, no server |
| T5 | `IFrameOverlay` seam in `MediaFoundationVideoWallpaperPlayer.Tick()`, no-op default, overlay failure contained | Interop | xUnit with a fake overlay |
| T6 | `Direct2DAlertOverlay`: lazy D2D / DirectWrite resources on the player thread, rebuild on device change, draws plain tiles from the layout; CsWin32 entries | Interop | Manual + measured GPU |
| T7 | Alert visuals ported from great-sage: warning / failed bands, title, modules, timed in/out animation driven by the frame clock | Interop | Manual, side by side with the page |
| T8 | Wiring in `AppComposition`: pipe → queue → overlay; `alerts-enabled` key in `settings.conf` | App | Wiring tests with fakes |
| T9 | Supervised hardware run: combined commands, Explorer restart, back-to-back alerts, covered desktop, GPU counters | — | Evidence in the feature document |

**T0 is a gate.** If Direct2D on the back buffer does not show, or breaks after the device is
re-created, stop and rethink before any production code.

## 7. Risks

1. Direct2D on the flip-model back buffer inside Progman/WorkerW is unproven here **[inferred]**.
   T0 settles it.
2. Device loss / swapchain re-creation must drop and rebuild every Direct2D resource; a stale
   target bitmap would fail every tick silently because `Tick()` swallows exceptions
   **[inferred]**. T6 logs the first failure.
3. The pixelated copy effect of the page (`drawFailureLayer`) has no one-call equivalent; v1 may
   ship without it and add it later with a Direct2D scale effect **[inferred]**.
4. Multi-monitor is out of scope for v1.
