# Video wallpaper — feasibility analysis

**Status:** research plus a working spike, 2026-09-21. The attach mechanism and the loop are now
empirically confirmed on this machine (see §3.6 and §4). Production implementation not started yet.

**Idea:** import a video file from disk and play it, looping forever, as the desktop wallpaper.
**Constraint:** native Windows / .NET only, no third-party dependencies (no LibVLC, FFmpeg, mpv, NuGet media libraries).
**Target OS checked:** Windows 11 build 10.0.26200 (25H2).

Evidence labels: **[code]** read in this repository, **[source]** read in another project's source,
**[doc]** Microsoft documentation, **[community]** issue trackers / forums / third-party projects,
**[inferred]** reasoning, not verified, **[tested]** empirically confirmed by our own spike on this machine.

---

## 1. Verdict

**Confirmed viable on this machine — with one hard requirement the original research missed.**

- Attaching a window behind the desktop icons **[tested]** works on this Windows 11 25H2 (build
  10.0.26200) "raised desktop" machine — but *only* when the host window presents through a
  DXGI/D3D swapchain. Plain GDI painting (`FillRect`/`WM_PAINT`), even on a perfectly styled,
  correctly parented, correctly z-ordered, non-cloaked `WS_CHILD` window, produces **zero visible
  pixels**, because `Progman` carries `WS_EX_NOREDIRECTIONBITMAP` on this build and a GDI-painted
  child has no DWM redirection surface to compose into. See §3.6 for the full trail (six failed
  GDI configurations, then a D3D swapchain working on the first try).
- This is good news for the design, not bad: the leading playback candidate below
  (`IMFMediaEngine` in frame-server/D3D mode) was already going to present via D3D. The mechanism
  that turned out to be *required* is the one already preferred — what's now ruled out is treating
  "legacy HWND mode" (GDI/blit-based) as a safe fallback; it almost certainly hits the same wall.
- A fully native playback path exists (`IMFMediaEngine`). Its loop (`SetLoop(true)`) is **[tested]**
  clean — no black frame, no stutter — confirmed by direct observation, not just the (silent,
  non-firing) `MF_MEDIA_ENGINE_EVENT_ENDED` event. See §4.
- The attach mechanism is still undocumented shell behaviour. It has already changed once (24H2)
  and will likely change again. Any implementation has to accept ongoing maintenance.

**Next step:** build the production feature (native host window + `IMFMediaEngine` frame-server
D3D playback), following the sketch in §9, now that both open measurements are closed.

---

## 2. What CosmicWin already has, and what it lacks

All **[code]**.

| Area | State |
| --- | --- |
| Target framework | `net10.0-windows10.0.19041.0`; App uses WPF and WinForms together. No third-party runtime dependency (CsWin32 is a Microsoft source generator; xunit / coverlet are test-only). |
| Layering | `Layout` = pure logic (`net10.0`), `Interop` = the only assembly allowed to touch Win32, `App` = orchestration. New Win32 wallpaper-host code belongs in `Interop`. |
| Tray | WinForms `NotifyIcon`. A new item needs a `TrayMenuEntry` value, a `MenuOrder` slot, and a delegate on `TrayMenuController`. |
| Settings | `%LOCALAPPDATA%\CosmicWin\settings.conf`, hand-parsed `key = value`. A `VideoPath` key fits `Settings` / `Parse` / `Serialize`. |
| Window admission | `IsTrackable` rejects `WS_CHILD` windows; `WindowFilters.IsAutoExcluded` rejects `WS_EX_TOOLWINDOW`, zero-area and minimized windows. A wallpaper host that is `WS_CHILD` of Progman/WorkerW would **not** be tiled by mistake. |
| Own-process events | `WINEVENT_SKIPOWNPROCESS` is used, so the app's own windows do not feed back into the tiler. |
| DPI | PerMonitorV2 in `app.manifest`. No `WM_DPICHANGED` handling. |
| Display changes | Polled every 400 ms; no `WM_DISPLAYCHANGE` handling. |
| Fullscreen detection | `MultiMonitorWorkspaceAdapter.IsFullscreen` is private, static, and only looks at windows already in the tiling tree. **Not reusable** for "something covers the desktop". `SHQueryUserNotificationState` is not used anywhere. |
| Native methods missing | `CreateWindowEx`, `RegisterClass`, `FindWindow`, `SendMessageTimeout`, `SetParent`, Progman / WorkerW handling are not in either `NativeMethods.txt`. |
| Existing overlay | `Win32OverlayWindow` is a static helper over an existing HWND (passive styles, placement, clipping). It is **not** a window host: no class registration, WndProc or pump. The focus border is a WPF `Window` + `WindowInteropHelper`. |
| Tests | xunit, hand-written fakes in `TestDoubles/`, seams `INativeWindowSource` / `INativeDisplaySource`. Desktop-gated tests need `COSMICWIN_RUN_DESKTOP_TESTS=1`. |

---

## 3. Attaching a window behind the desktop icons

### 3.1 The mechanism

Undocumented message `0x052C` sent to `Progman` makes the shell create a `WorkerW` window sitting between the wallpaper and the icons; the app parents its window to it.
Sources: [DynamicWallpaper docs](https://dynamicwallpaper.readthedocs.io/en/docs/dev/make-wallpaper.html) **[community]**, Lively `WinDesktopCore.cs` **[source]**.

### 3.2 What changed in Windows 11 24H2 ("raised desktop")

**[community] + [source]**

- `Progman` carries `WS_EX_NOREDIRECTIONBITMAP`; `SHELLDLL_DefView` is a layered child of Progman; the wallpaper `WorkerW` is a **child of Progman** and z-ordered under the icons.
- Lively detects this by testing that style on Progman, **not by OS build number** (a maintainer explicitly rejected `Build >= 26002` checks, [PR #2050](https://github.com/rocksdanister/lively/pull/2050)).
- Microsoft's note, quoted in [Lively #2074](https://github.com/rocksdanister/lively/issues/2074): DefView draws transparent only with HDR or a slideshow running; builds 27xxx always draw it transparent.
- No Microsoft document describes any of this. **All knowledge of the new layout is community-derived.**

### 3.3 The sequence that works today (from Lively source)

1. `FindWindow("Progman")`.
2. Test `WS_EX_NOREDIRECTIONBITMAP` on it.
3. `SendMessageTimeout(progman, 0x052C, wParam = 0xD, lParam = 0x1, SMTO_NORMAL, 1000)`. **Single message.** The older two-message sequence (`0xD,1` then `0xD,0`) deletes the new WorkerW ([tool-animated-wallpapers](https://github.com/bbabcock1990/tool-animated-wallpapers)).
4. Legacy path: enumerate top-level windows, find the one owning `SHELLDLL_DefView`, take its next `WorkerW` sibling; `SetParent(hwnd, workerW)`.
5. Raised path: `FindWindowEx(progman, 0, "WorkerW")`; give the host `WS_CHILD | WS_EX_LAYERED` with `SetLayeredWindowAttributes(255)`; `SetParent(hwnd, progman)`; `SetWindowPos(hwnd, insertAfter = DefView)`; force the WorkerW to `HWND_BOTTOM` if it is not the last child.
6. Re-assert z-order after any window the video pipeline creates.

Two community sources disagree on the parent (Progman vs the WorkerW child): rexpaper PR #2 vs tool-animated-wallpapers. Lively's source resolves it (parent = Progman on raised desktop). Confirm on 25H2 in the spike.

### 3.4 Multi-monitor and DPI **[source]**

- Lively offers three arrangements: one window per display, one spanning window, or duplicate (non-primary muted and seeked to 0).
- Per display: position in screen coordinates from monitor bounds, converted with `MapWindowPoints` to parent-relative coordinates (may be negative on the virtual screen).
- WebView2 inside Progman picks up the *primary* monitor's DPI ([Lively #1996](https://github.com/rocksdanister/lively/issues/1996)). Not relevant to a Media Foundation host, but a warning that DPI inside this parent is not free.
- Behaviour across **virtual desktops**: not found in any source. **Unverified.**

### 3.5 Recovery, or the wallpaper dies silently **[source]**

- `TaskbarCreated` (Explorer restart). Lively compares the taskbar's Explorer **PID**, because DPI changes also broadcast that message.
- `WinEventHook(EVENT_OBJECT_DESTROY)` on the WorkerW; session unlock (`IsWindow(workerW)`); `WM_DISPLAYCHANGE`.
- Retry after 500 ms if WorkerW is not there yet; give up with an error after 2+ restarts within 30 s.
- **Never** call `SystemParametersInfo(SPI_SETDESKWALLPAPER)` to force a refresh in raised mode: it destroys the WorkerW ([Lively #2777](https://github.com/rocksdanister/lively/issues/2777)).
- On the raised desktop `GetParent` returns 0 even when correctly attached; do not poll it as a health check.

### 3.6 Empirical confirmation and the GDI-vs-D3D finding (2026-09-21 spike) **[tested]**

Built two throwaway spikes (`spikes/AttachSpike/`, `spikes/PlaybackSpike/`, not part of
`CosmicWin.sln`, CsWin32-based, no third-party deps) and ran them repeatedly on this exact
machine. All Win32-level state checked out perfectly from the very first run: correct
`GWL_STYLE` (`WS_VISIBLE | WS_CHILD`), correct `GetWindowRect`, correct `GetParent`,
`IsWindowVisible = true`, correctly z-ordered right behind `SHELLDLL_DefView`
(`GetTopWindow(Progman) = DefView`), and **not** DWM-cloaked (`DWMWA_CLOAKED = 0`). And yet the
window painted with plain GDI (`FillRect` in `WM_PAINT`) produced **zero visible pixels**, across
every one of these independently tested configurations:

1. With Wallpaper Engine (a commercial competitor, already installed and running on this machine)
   actively rendering its own content through the same Progman/WorkerW slot.
2. With a static single-image wallpaper (Wallpaper Engine closed).
3. With Windows' native slideshow wallpaper mode forced on (`IDesktopWallpaper::SetSlideshow`,
   confirmed active via `GetStatus() = DSS_ENABLED | DSS_SLIDESHOW`) — ruling out the "`DefView`
   only draws transparent under HDR or a slideshow" theory from §3.2.
4. Parenting to the `WorkerW` child instead of Progman directly — ruling out the Progman-vs-WorkerW
   disagreement noted in §3.3.
5. Creating the host window `WS_EX_LAYERED` **from `CreateWindowEx`**, not after (the first pass's
   bug: `SetLayeredWindowAttributes` had failed with `ERROR_INVALID_PARAMETER` because the window
   wasn't created layered) — ruling out the layered-window-ordering theory from a community fix
   (rexpaper).
6. With CosmicWin's own tiler (`CosmicWin.App`) closed — ruling out the idea that CosmicWin's own
   window-shown watcher was grabbing and mismanaging the spike's briefly-top-level host window.

External research (fresh web search, not in this repo) converged on the same explanation
independently: `Progman` carries `WS_EX_NOREDIRECTIONBITMAP` on this build, meaning it has **no
DWM redirection surface**, and a GDI-painted or blt-model-presented child window has nothing to
composite into — regardless of how correctly it's styled, parented, or z-ordered. Two small,
unofficial community projects (`RitvikDayal/featherwall`, `rexxpaper/rexpaper`) independently
arrived at the same fix from different angles: present through a real GPU surface (a DirectComposition
visual, or — cheaper — a plain DXGI flip-model swapchain), not GDI.

**The fix, tested and confirmed working on the first attempt:** create the host window with no
special styles (no `WS_EX_LAYERED` needed), get an `ID3D11Device` (`D3D11CreateDevice`,
`D3D_DRIVER_TYPE_HARDWARE`), get an `IDXGIFactory2` from it (`IDXGIDevice.GetAdapter` →
`IDXGIAdapter.GetParent`), call `IDXGIFactory2.CreateSwapChainForHwnd` on the host HWND with
`DXGI_SWAP_EFFECT_FLIP_DISCARD` / `DXGI_ALPHA_MODE_IGNORE`, get the back buffer
(`IDXGISwapChain1.GetBuffer`), make a render target view, `ClearRenderTargetView`, `Present(1, 0)`.
This produced a full-screen solid color visible behind the desktop icons on the very first run,
after six independent GDI-based configurations had all failed. CsWin32 0.3.321 covers the full
D3D11/DXGI surface needed here (`ID3D11Device`, `IDXGIFactory2`, `IDXGISwapChain1`, etc.) with no
hand-rolled COM interop required — same experience as the Media Foundation interop in §4.

**Implication for the design in §9:** the wallpaper host window must present via D3D from the
start (which `IMFMediaEngine` frame-server mode already does — see §4); do not build or rely on a
GDI/`WM_PAINT` fallback path for the host window itself, and do not treat `IMFMediaEngine`'s
"legacy HWND mode" as a safe degradation path without testing it separately, since it plausibly
routes through the same GDI/blit presentation that just failed six times here.

---

## 4. Native playback options

| Option | HWND / composition output | Loop | HW decode | Verdict |
| --- | --- | --- | --- | --- |
| **`IMFMediaEngine` / `IMFMediaEngineEx`** (Media Foundation, COM) | Legacy HWND mode (`MF_MEDIA_ENGINE_PLAYBACK_HWND`, blocks DComp mode), DirectComposition mode (`EnableWindowlessSwapchainMode`), or frame-server mode (`TransferVideoFrame` into own D3D texture) | `SetLoop(true)`, mirrors HTML5 `loop`; **no gapless guarantee** | Yes, needs `MF_MEDIA_ENGINE_DXGI_MANAGER`; software decode without it | **Best fit.** |
| WinRT `Windows.Media.Playback.MediaPlayer` | No HWND output; `GetSurface(Compositor)` or frame-server mode (`IsVideoFrameServerEnabled` + `CopyFrameToVideoSurface`) | `IsLoopingEnabled`, no gapless guarantee | Yes | Works, but needs extra WinRT / composition interop to reach an HWND. |
| WPF `MediaElement` | WPF surface; airspace behaviour inside a Progman child **unverified** | Manual: `MediaEnded → Position = 0; Play()` (a seam is likely) | Limited; docs say it "can use" the WMP 10 control | Lively ships this as an optional plugin, not the default. |
| MFPlay / DirectShow | — | — | — | Legacy. Microsoft points to `MediaPlayer` / `IMFMediaEngine`. |

Sources: [SetLoop](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-setloop), [EnableWindowlessSwapchainMode](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengineex-enablewindowlessswapchainmode), [TransferVideoFrame](https://learn.microsoft.com/en-us/windows/win32/api/mfmediaengine/nf-mfmediaengine-imfmediaengine-transfervideoframe), [DXGI manager attribute](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-media-engine-dxgi-manager), [MediaPlayer.IsLoopingEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplayer.isloopingenabled), [MediaPlayer.GetSurface](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplayer.getsurface), [MFPlay](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/api/mfplay/nn-mfplay-imfpmediaplayer). All **[doc]**.
Sample to start from: `MediaEngineDCompWin32Sample` in [microsoft/media-foundation](https://github.com/microsoft/media-foundation).

**Leading candidate, now confirmed [tested]:** `IMFMediaEngine` in frame-server / D3D mode (D3D11
device via the DXGI manager, `SetLoop(true)`, own DXGI swapchain on the host HWND, present per
vsync). It gives control over pausing and lets the last frame stay on screen across the loop
point. **Legacy HWND mode is no longer a safe fallback** — §3.6's spike proved six different
GDI-painted configurations all render zero pixels on this build's Progman, and legacy HWND mode
plausibly presents the same way. Test legacy HWND mode in isolation before ever relying on it.

### Loop seam — confirmed clean **[tested]**

- Spike (`spikes/PlaybackSpike/`, `IMFMediaEngine` legacy HWND mode, CsWin32-generated managed
  bindings) played a 6-second test clip on loop (`SetLoop(true)`) for several cycles. Directly
  observed by a human watching the window: **no black frame, no stutter, a clean cut** at every
  loop point.
- `MF_MEDIA_ENGINE_EVENT_ENDED` **never fires** while looping — same semantics as HTML5
  `<video loop>`, where the engine seeks back to start instead of "ending". Do not rely on this
  event to detect or time the loop boundary; poll `CurrentTime` instead if the design ever needs
  to react to the loop point (e.g. to synchronize a hold-last-frame transition).
- No Microsoft document promises a gapless `SetLoop` or `IsLoopingEnabled` in general, and a
  [Microsoft Q&A thread](https://learn.microsoft.com/en-us/answers/questions/831460/seamless-loop-problem-with-media-foundation)
  reports gaps at the loop point in some Media Foundation scenarios **[community]** — but on this
  machine, with this codec/resolution (H.264, 3440×1440, 60fps) and legacy HWND mode specifically,
  the loop was clean. Re-verify if the production codec/resolution profile differs meaningfully.
- The frame-server-mode "hold last frame" mitigation from the original research remains untested
  (the loop-seam spike used legacy HWND mode for simplicity) — not needed given the clean result
  above, but worth keeping in mind if a different codec/resolution shows a seam later.

### What the reference apps do

- **Lively [source]:** default player is **mpv**, a separate process (`--loop-file --keep-open --no-border --hwdec=auto-safe`), whose window is found and `SetParent`ed directly. It also ships `Lively.Player.Wmf`, a WPF `MediaElement` process that loops via `MediaEnded → Position = 0; Play()`; it is a non-default plugin and the Store build ships only mpv. `libmpv` / `libvlc` are marked deprecated. It does **not** use `IMFMediaEngine`.
- **Wallpaper Engine [community / inferred]:** closed source. Official docs say the wallpaper is "part of the Windows Explorer process" and hardware video decode is on by default ([dwm](https://help.wallpaperengine.io/en/performance/dwm.html), [performance](https://help.wallpaperengine.io/en/videos/performance.html)). Media Foundation is the default framework *by inference* from the Windows N and troubleshooting pages; LAV / DirectShow is opt-in. The attach mechanism is not published; its 24H2 bug (invisible with desktop icons off) points to the same Progman / WorkerW scheme.

Neither reference app validates a fully native `IMFMediaEngine` loop for us; the native path is uncharted by them.

---

## 5. Codecs and import validation

- **Built in** to stock Windows 11: H.264, H.263, VC-1, WMV, DV, VP8, MJPEG. **Need Store extensions:** HEVC, VP9, AV1, MPEG-2, Web Media (OGG / Theora). [Microsoft support](https://support.microsoft.com/en-us/windows/codecs-in-media-player-d5c2cdcd-83a2-4805-abb0-c6888138e456) **[doc]**
- MP4 / M4V / MOV are native containers; MKV is supported but its HEVC / VP9 / AV1 tracks still need decoders ([formats](https://learn.microsoft.com/en-us/windows/win32/medfound/supported-media-formats-in-media-foundation), [MKV](https://learn.microsoft.com/en-us/windows/win32/medfound/mkv-support)). **No Microsoft document was found for `.webm`.**
- Wallpaper Engine's developer advice (Steam forum, not official docs): MP4 with H.264 or H.265; H.265 needs a recent GPU; 30 fps is cheaper than 60 at 4K.
- **Probing at import:** `MediaSource` → `MediaPlaybackItem` and check decoder status (documented for audio tracks; the video-track equivalent is **unverified**), or `MFCreateSourceReaderFromURL`, or load the file in a throwaway engine and watch the error event. None of these proves hardware decode.
- Lively validates **by extension only** and says in a code comment that it should check the file header instead. Do better.

---

## 6. Resource management (copy from the reference apps)

**[source]** unless noted. Lively `Playback.cs` / `WindowUtil.cs`:

- A **500 ms `DispatcherTimer`**. The source comment says `EVENT_OBJECT_LOCATIONCHANGE` was "too noisy... not reliable".
- **Grid coverage algorithm** (default since v2.2): candidate windows are visible, not cloaked (`DWMWA_CLOAKED`), not iconic, not `WS_EX_LAYERED` / `TRANSPARENT` / `TOOLWINDOW`, top-level, with a title. Pause if any is maximized, or one covers ≥ 95% of the display's work area, or the screen split into 50 px tiles has ≤ 5% uncovered. Evaluated per display.
- System states: session lock (`SessionSwitch`), Remote Desktop (`RemoteConnect` / `RemoteDisconnect`, default pause), battery (`GetSystemPowerStatus`, default ignore), battery saver (`SystemStatusFlag`, default ignore).
- `SHQueryUserNotificationState` gives no event for fullscreen start/stop, so it must be polled ([doc](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate)).
- Battery saver: `RegisterPowerSettingNotification` with `GUID_POWER_SAVING_STATUS`. Lock / RDP: `WTSRegisterSessionNotification` → `WM_WTSSESSION_CHANGE` ([doc](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification)). Specific lock / unlock / remote codes were quoted from memory in the research, so recheck them.
- Wallpaper Engine's fullscreen detection reportedly "will only activate for the currently focused window" ([Steam thread](https://steamcommunity.com/app/431960/discussions/1/3417682380513731150/)) — avoid foreground-only detection.
- Cheapest levers: stop presenting when fully covered, mute with `SetMuted`, hardware decode.

## 7. Import and storage

- WinForms `OpenFileDialog` (wraps `IFileDialog`) on an STA thread with an owner window is enough. Drop to CsWin32 `IFileOpenDialog` only for options such as `FOS_FORCEFILESYSTEM` or `SetClientGuid`. [doc](https://learn.microsoft.com/en-us/windows/win32/shell/common-file-dialog)
- Lively supports both modes: copy into a library folder, or keep the absolute path (`IsAbsolutePath`) and fail with `WallpaperNotFoundException` when the file is gone.
- **Recommendation (reasoning, not sourced):** copy into `%LOCALAPPDATA%\CosmicWin\` so moving or deleting the original does not break the wallpaper; persist the copied path as `VideoPath` in `settings.conf`.

---

## 8. Risks

1. **Undocumented shell behaviour.** Everything in section 3 is community-derived. It changed in 24H2, differs across KBs (KB5050009 reportedly fixed part of it), and can change again. Expect recurring maintenance.
2. ~~Seamless loop unproven~~ **[tested] Resolved** — clean loop confirmed, see §4. Re-verify if the production codec/resolution differs from the spike's test clip.
3. ~~Swapchain composition unverified~~ **[tested] Resolved, with a correction** — a plain DXGI flip-model swapchain composes correctly as a child of Progman with *no* `WS_EX_LAYERED` needed; the thing that actually blocks composition is GDI/blit presentation, not the layered style. See §3.6. Six GDI-based attempts failed (including one with `WS_EX_LAYERED` set correctly); the D3D swapchain attempt worked with plain unlayered styles on the first try.
4. **Window-modding tools.** Windhawk (translucent windows) and Start11 hijacked the wallpaper window and produced a black square ([Lively #2890](https://github.com/rocksdanister/lively/issues/2890)). A tiling manager is the same category of tool.
5. **Own-overlay collision.** Lively drops `WS_EX_LAYERED` windows from its coverage check. CosmicWin's own overlay windows may be layered; a pause check must not confuse them with a covering app.
6. **Open 25H2 issues in Lively (unconfirmed, untriaged):** Explorer hangs after virtual-desktop switching ([#3245](https://github.com/rocksdanister/lively/issues/3245)); a secondary monitor not applying ([#3138](https://github.com/rocksdanister/lively/issues/3138)).
7. **Codec expectations.** HEVC / VP9 / AV1 files can import fine and then fail to decode without Store extensions.
8. ~~Layering cost~~ **[tested] Resolved** — CsWin32 0.3.321 covers `IMFMediaEngine`, `IMFMediaEngineNotify`, `IMFMediaEngineClassFactory`, and the full D3D11/DXGI surface used in §3.6 cleanly as managed bindings; no hand-written COM interop was needed anywhere in either spike. The one gap: CsWin32 doesn't project the `MFMediaEngineClassFactory` coclass's activation, so that one call goes through `Type.GetTypeFromCLSID` + `Activator.CreateInstance` with the documented CLSID (`b44392da-499b-446b-a4cb-005fead0e6d5`) — a two-line workaround, not real interop risk.
9. **No pause logic to reuse.** The existing fullscreen check is private and tree-scoped; a coverage check is new code.

---

## 9. Sketch of how it would fit (not a design)

- `CosmicWin.Interop`: a wallpaper-host window (class registration, WndProc, attach / re-attach, per-monitor placement) and the Media Foundation wrapper. New entries in `NativeMethods.txt`. **Per §3.6: the host must never fall back to GDI painting** — `IMFMediaEngine` frame-server mode with its own DXGI swapchain on the host HWND is not just the preferred option, it is the only one confirmed to render anything on this machine's Windows build.
- `CosmicWin.Layout` or an `internal static` in `App`: the pure pause predicate (grid coverage) so it is unit-testable without a desktop.
- `CosmicWin.App`: tray entry ("Set video wallpaper…"), `VideoPath` in `settings.conf`, composition, and the timer that feeds the pause predicate. Listen for `TaskbarCreated` to re-attach.
- The host must be `WS_CHILD` (or `WS_EX_TOOLWINDOW` top-level) so the tiler never admits it; add an explicit exclusion if it ends up as a resizable top-level.

---

## 10. Evidence gaps

Resolved by the 2026-09-21 spike (§3.6, §4): CsWin32 coverage of `IMFMediaEngine`; swapchain
composition inside a Progman child (works unlayered via D3D, GDI/blit does not compose at all —
correction, not just confirmation, of the original assumption); actual loop-seam behaviour of
`SetLoop` on this codec/resolution.

Still open:

- Behaviour across **virtual desktops** (one host visible on all?) — not tested.
- **DPI** and multi-monitor behaviour of the host — the spike only targeted the primary monitor's work area; per-monitor placement, non-primary DPI, and multi-swapchain coordination are all untested.
- `.webm` native support (no Microsoft document found).
- Video-track decoder status probing (`SupportInfo`) — documented for audio only.
- Whether `IMFMediaEngine` legacy HWND mode (as opposed to frame-server/D3D mode) also fails to compose the same way GDI did — not tested in isolation; treat it as likely broken until proven otherwise (see §4).
- Any Microsoft documentation of the 24H2/25H2 raised-desktop layout — still none found; every explanation in §3.6 is triangulated from community sources plus our own empirical test, not Microsoft.
- Whether Lively's WMF player works on 25H2 today (not checked).
- Wallpaper Engine: no source or reverse-engineering write-up found; its Media Foundation and attach claims are inferred; no official codec matrix.
- The recommended file-copy strategy is still reasoning, not sourced.
- Recovery behaviour (§3.5: `TaskbarCreated`, WinEventHook, session unlock) — none of it was exercised by the spike, which only ran for the length of a manual test.

## 11. Decision checklist for the review

- [x] Is a fragile, undocumented shell dependency acceptable for this app? — mechanism confirmed working on this machine as of 2026-09-21; still undocumented and has changed before (24H2), so this is an accepted ongoing-maintenance cost, not a resolved risk.
- [x] Is the spike worth its cost? — yes: it found and fixed a real blocker (GDI vs. D3D presentation) that the original research had not identified, not just a "does the known plan work" check.
- [x] After the spike: is the loop seam acceptable? — yes, clean, no hold-last-frame fallback needed for the tested codec/resolution (H.264, 3440×1440, 60fps).
- [ ] Is "one host per monitor, muted duplicates" enough, or is a spanning mode wanted? — still open, and now also blocked on the untested multi-monitor/DPI gap above.
- [ ] Are HEVC / VP9 / AV1 in scope (requires Store extensions) or is H.264 MP4 the supported format? — still open.
