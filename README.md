<img src="docs/image/icon-256.png" alt="CosmicWin" width="120" align="right" />

# CosmicWin

A tiling window manager for Windows 11, modelled on COSMIC's tiling behaviour.

Windows are arranged in a tree and given the whole work area between them. Focus, movement, resizing
and Windows' own virtual desktops are driven from the keyboard.

## What works

- **Tiling** — every window gets a share of the work area, with a configurable uniform gap
  (`gap` in `settings.conf`, default 8, 0–64 pixels). The same gap draws around and between an
  alert's tiles too.
- **Focus** — move between windows by direction.
- **Movement** — move a window through the layout. The walk climbs the tree, so a window leaves its
  group when it runs out of room rather than dead-ending, and the walk is reversible.
- **Resize** — grow toward a neighbour, or shrink when there is none.
- **Virtual desktops** — switch by number, send a window to one, each with its own layout. A new
  window opens on the desktop you are on, even when Windows would have put it somewhere else.
- **Dialogs float** — a modal opens centred at its own size and is never tiled. The move chord snaps
  it to half the screen or the whole work area instead of walking the tree, and returns it to the
  size it opened at. Resize does nothing while one holds the foreground, rather than rearranging the
  window behind it.
- **Focus border** — the active window is outlined in the system accent colour, thicker than the
  one Windows draws, with corners matching its own.
- **Windows that fight back** — a window dragged out of its slot snaps back on drop; a window that
  resizes itself is put back; a window that refuses to be positioned is left alone rather than
  fought.
- **Start at logon** — an opt-in Scheduled Task starts CosmicWin elevated when you log on, without a
  UAC prompt every time.

## What it does not do yet

- **One monitor.** The layout engine is monitor-aware and multi-monitor requirements exist, but
  nothing beyond a single display is exercised or claimed.
- **An emptied virtual desktop is not removed.** Deliberate — see the notes.
- **No configuration file.** Keybindings are compile-time.

## Requirements

- Windows 11 (developed against build 26200)
- .NET 10 SDK

CosmicWin runs elevated: it manages windows belonging to other processes, and Windows will not allow
that from a normal process.

## Build and run

```powershell
git clone <this repo>
cd CosmicWin
./scripts/run.ps1
```

`run.ps1` builds, copies the output to a git-ignored `run/`, and launches that copy elevated — accept
the UAC prompt. Running the copy leaves the build tree unlocked, so builds and tests keep working
while the app is open. Exit from the tray icon.

## Recommended Windows setting

Turn off **Settings → System → Multitasking → "Snap windows"**. Windows' own edge snapping competes
with the tree: dragging a window to an edge hands it half the screen behind CosmicWin's back, and the
snap layout flyout appears over a work area CosmicWin has already divided. With it off, a dragged
window does what CosmicWin says it does. The setting takes effect at your next sign-in.

CosmicWin never touches this setting itself — it is yours to set, and it stays set after CosmicWin
exits. Nothing depends on it either: the guards that keep a maximized or self-resizing window from
breaking the layout run the same whether Snap is on or off, because maximize also arrives from the
maximize button, a double click on the title bar, and `Win+Up`.

## Start at logon

CosmicWin does not install itself. Autostart is opt-in, and it is a Scheduled Task rather than a
`Run` registry key because the app must start elevated — a `Run` entry cannot, and would hand you a
UAC prompt at every logon.

```powershell
CosmicWin.exe --install-task     # register the logon task
CosmicWin.exe --uninstall-task   # remove it
```

Both commands do their work and exit immediately; neither starts the window manager. Run them from
an elevated shell — registering a task that runs with highest privileges is itself a privileged
operation.

Four things worth knowing before you rely on it:

- **The task points at the executable you invoked**, resolved at install time. Install from the copy
  you actually run — `run\CosmicWin.exe` after `run.ps1`, not the build tree, which `run.ps1`
  deliberately leaves unlocked. Move or delete that copy and the task starts nothing.
- **Quitting from the tray disables the trigger.** Exiting is read as "not right now", so the task
  stays registered but stops firing, and the next logon is quiet. Re-run `--install-task` to turn it
  back on; it re-registers over the existing task and re-enables the trigger.
- **`--uninstall-task` is idempotent.** A task that was never installed counts as success, so it is
  safe to run twice, or on a machine you are not sure about.
- **The task XML is written to** `%LOCALAPPDATA%\CosmicWin\CosmicWinTask.xml`. It is an artefact of
  installation, not a configuration file — editing it changes nothing until the next install, which
  overwrites it.

## Settings

CosmicWin has no installer: the first time it runs, it writes `%LOCALAPPDATA%\CosmicWin\settings.conf`
itself, with every default already filled in, before anyone has touched a single setting. An existing
file is never rewritten except by a tray-menu save, a settings-changing HTTP request (the wallpaper
scene route, below), or a hand edit of your own — and a hand edit takes effect at CosmicWin's next
start.

| Key | Default | Meaning |
|---|---|---|
| `focus-border` | `on` | Draw CosmicWin's own thicker focus border. |
| `border-color` | `accent` | `#RRGGBB`, or `accent` to follow Windows' own accent colour. |
| `tiling` | `on` | Lay windows out at all; off leaves them where they open. |
| `video-wallpaper-path` | *(blank)* | Absolute path to the imported video wallpaper. Set by the tray menu or the video HTTP route, not meant to be hand-edited. |
| `alerts-enabled` | `on` | Accept live alert commands over the named pipe. |
| `alert-http` | `on` | Also accept alert commands over the loopback HTTP endpoint. |
| `alert-http-port` | `47811` | The loopback TCP port the HTTP endpoint listens on. |
| `video-wallpaper-http` | `off` | Accept a video-wallpaper switch over the same HTTP endpoint. |
| `gap` | `8` | Whole pixels of space around and between tiled windows and alert tiles (0–64). |
| `wallpaper-mode` | `html` | `html` shows an animated scene wallpaper; `video` loops the configured video instead; `mini` shows no wallpaper and puts a small scene window in a screen corner (see Mini scene window, below). |
| `wallpaper-scene` | `processing` | Which html scene to show: `processing`, `explorer`, `idle` or `raphael`. |
| `wallpaper-fps` | `60` | Caps the html wallpaper's own frame rate: `30` or `60`. |
| `wallpaper-scene-http` | `on` | Accept a wallpaper-scene switch over the same HTTP endpoint. |
| `mini-corner` | `top-right` | Where the mini scene window sits: `top-left`, `top-right`, `bottom-left` or `bottom-right`. Alt+M changes it. |

Every HTTP-related key above is served by the same local HTTP server, sharing one port and one
bearer-token file — see Alerts, below, for how that endpoint is gated.

## Alerts

CosmicWin can flash a `warning` or `failed` alert over the desktop, for example when a build breaks.
Alerts are on by default (`alerts-enabled = on` in `%LOCALAPPDATA%\CosmicWin\settings.conf`). Each
command asks for 1–16 tiles per kind (16 in total) and an optional duration of 1–60 seconds
(default 5).

From a terminal, `CosmicWinAlert.exe` sends a command over a per-user named pipe:

```powershell
CosmicWinAlert.exe warning:2 failed:1 duration:5
```

### Over HTTP (localhost only)

Other programs on the same PC can send the same command over HTTP. The endpoint is **on by
default** (`alert-http = on`), loopback-only (`127.0.0.1` / `localhost`, never reachable over the
network) and gated by a bearer token nothing outside this machine can read. Turn it off in
`settings.conf` and restart CosmicWin:

```ini
alert-http = off
```

Since `alert-http` defaults to on (see Settings, above), this port is already open on a fresh install
unless you turn it off. On first start CosmicWin also writes a random token to
`%LOCALAPPDATA%\CosmicWin\alert-http.token`, kept across restarts; delete the file to get a new one
on the next start.

```powershell
$token = Get-Content "$env:LOCALAPPDATA\CosmicWin\alert-http.token"
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47811/v1/alerts `
  -Headers @{ Authorization = "Bearer $token" } `
  -ContentType 'application/json' -Body '{ "warning": 2, "failed": 1, "duration": 5 }'
```

```sh
curl -X POST http://127.0.0.1:47811/v1/alerts \
  -H "Authorization: Bearer $(cat "$LOCALAPPDATA/CosmicWin/alert-http.token")" \
  -H "Content-Type: application/json" \
  -d '{"warning":1}'
```

The body is a JSON object with `warning`, `failed` and `duration`, all integers and all optional.
At least one of `warning` or `failed` is required. The request is checked by the same rules as the
pipe.

Every request to every route on this server — alerts, video wallpaper, wallpaper scene — is checked
in the same fixed order, cheapest first: the connection must be loopback; there must be no `Origin`
header (any value means a browser sent it); the `Host` header must be exactly `127.0.0.1:<port>` or
`localhost:<port>` (blocking DNS rebinding); the path must name a route that is turned on; the
method must be `POST`; the bearer token must match; and `Content-Type` must be `application/json` —
only then is the body itself parsed and validated.

| Status | Meaning |
|---|---|
| 202 | Queued; body `ok` |
| 400 | Bad JSON, unknown field, or a command the alert rules reject |
| 401 | Missing or wrong bearer token |
| 403 | Request from a browser (`Origin` header), a foreign `Host`, or not from this PC |
| 404 / 405 | An unknown path, a route that is turned off, or any method other than `POST` |
| 413 | Body larger than 1 KB |
| 415 | `Content-Type` is not `application/json` |
| 503 | Alerts are turned off |

If the port is already in use, the HTTP endpoint (all three routes) stays off and the named pipe
keeps working for alerts. The reason is written to CosmicWin's desktop trace.

## Video wallpaper

Loops a single MP4 as the desktop wallpaper (`wallpaper-mode = video`), instead of the default html
scene wallpaper described under HTML wallpaper, below.

### Picking a video

The tray menu's **Wallpaper de video...** entry opens a file picker restricted to `.mp4` files. The
picked file is imported into `%LOCALAPPDATA%\CosmicWin\video-wallpaper<ext>` before it plays: when
the source is on the same drive as `%LOCALAPPDATA%`, the import is a hard link, so switching to a
multi-gigabyte file is instant; on a different drive, or if linking fails for any reason, CosmicWin
falls back to copying the file, which can take minutes for a large video. Either way the wallpaper
keeps playing if you later move or delete the original — a hard-linked import is the same data as
the original, so editing the source file in place changes the wallpaper too. Re-picking overwrites
the previous import; there is only ever one active video, recorded at `video-wallpaper-path` in
`settings.conf`.

If Explorer restarts (a crash, or `explorer.exe /restart`), CosmicWin listens for the shell's own
`TaskbarCreated` broadcast and re-attaches the video host to the desktop automatically, without
losing the running video.

### Over HTTP

Another program on the same PC can also switch the video, by sending the absolute path of a video
that is already on this PC. The route is **off by default** and independent of alerts. Turn it on in
`settings.conf` and restart CosmicWin:

```ini
video-wallpaper-http = on
```

It shares the server, port (`alert-http-port`) and token file (`alert-http.token`) with the alert
endpoint, and the same localhost-only rules apply.

```powershell
$token = Get-Content "$env:LOCALAPPDATA\CosmicWin\alert-http.token"
$body = @{ path = 'D:\Videos\space.mp4' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47811/v1/wallpaper/video `
  -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body
```

```sh
curl -X POST http://127.0.0.1:47811/v1/wallpaper/video \
  -H "Authorization: Bearer $(cat "$LOCALAPPDATA/CosmicWin/alert-http.token")" \
  -H "Content-Type: application/json" \
  -d '{"path":"D:\\Videos\\space.mp4"}'
```

The body is a JSON object with one field, `path`. The path must be absolute and start with a drive
letter and a backslash (`C:\...`). Relative paths, forward slashes, network shares, network drives,
device paths and URLs are rejected. Nothing is ever downloaded.

The request is answered as soon as the path is checked. The switch itself then runs in the
background, exactly like a pick from the tray menu — see Picking a video, above, for the hard-link
import and what it guarantees. If the switch fails, the previous video keeps playing. The outcome is
written to the desktop trace, never the path.

| Status | Meaning |
|---|---|
| 202 | Accepted; the switch runs in the background. Body `ok` |
| 400 | Bad JSON, unknown field, or a path that is not an absolute local drive path |
| 401 / 403 / 405 | As for alerts |
| 404 | The file does not exist, or the route is turned off (the body tells which) |
| 413 | Body larger than 4 KB |
| 415 | The file is not an `.mp4`, or `Content-Type` is not `application/json` (the body tells which) |
| 503 | This CosmicWin has no video wallpaper to switch, or `wallpaper-mode = html` is on (the default) |

In html mode (`wallpaper-mode = html`, the default) the video route answers 503 and never starts a
video, and the tray menu's video pick does nothing. Both write a `skipped reason=html-mode` line to
the desktop trace.

## HTML wallpaper

CosmicWin's desktop wallpaper is this html scene by default (`wallpaper-mode = html`); set
`wallpaper-mode = video` in `settings.conf` to loop a video wallpaper instead (see Video wallpaper,
above). The scene is rendered through a permanently preloaded WebView2 layer rather than a browser
window, and settings take effect at startup — changing `wallpaper-mode` or `wallpaper-scene` by hand
needs a restart; the HTTP route below exists for switching the scene without one.

### Modes, scenes and frame rate

Four scenes ship, chosen by `wallpaper-scene` in `settings.conf`: `processing` (the default),
`explorer`, `idle` and `raphael`. `wallpaper-fps` caps the scene's own frame rate at `30` or `60`
(default `60`); before this setting existed the scene drew uncapped, at the display's own refresh
rate.

Alerts render through the same preloaded WebView2 layer as the scene itself: each `warning`/`failed`
tile's letters are see-through, showing the running scene's own animation moving inside the letter
shapes, rather than a flat colour overlay. Every scene has its own hook for this effect.

### Switching the scene over HTTP

Another program on the same PC can switch the scene live over HTTP, without restarting CosmicWin.

The route is **on by default**, independent of the other two routes: since the html wallpaper is
CosmicWin's default renderer, a fresh install already accepts scene switches on this loopback-only
port. Turn it off in `settings.conf` and restart CosmicWin:

```ini
wallpaper-scene-http = off
```

It shares the server, port (`alert-http-port`) and token file (`alert-http.token`) with the alert
endpoint, and the same localhost-only rules apply.

```powershell
$token = Get-Content "$env:LOCALAPPDATA\CosmicWin\alert-http.token"
$body = @{ scene = 'idle' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47811/v1/wallpaper/scene `
  -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body $body
```

```sh
curl -X POST http://127.0.0.1:47811/v1/wallpaper/scene \
  -H "Authorization: Bearer $(cat "$LOCALAPPDATA/CosmicWin/alert-http.token")" \
  -H "Content-Type: application/json" \
  -d '{"scene":"idle"}'
```

The body is a JSON object with one field, `scene`: one of `processing`, `explorer`, `idle` or
`raphael`, in any letter case. The request is answered as soon as the name is checked; the switch
then runs on the UI thread and the new scene is saved to `wallpaper-scene` in `settings.conf`, so it
survives a restart. Asking for the scene already showing is accepted and changes nothing. If the
switch fails, the current scene stays and nothing is saved; the failure is written to the desktop
trace.

| Status | Meaning |
|---|---|
| 202 | Accepted; the switch runs on the UI thread. Body `ok` |
| 400 | Bad JSON, unknown field, or a scene name that is not one of the four |
| 401 / 403 / 405 | As for alerts |
| 404 | The route is turned off |
| 413 | Body larger than 256 bytes |
| 415 | `Content-Type` is not `application/json` |
| 503 | Not in html or mini mode (`wallpaper-mode = video`), or this CosmicWin has no scene page or mini window to switch |

## Mini scene window

Set `wallpaper-mode = mini` in `settings.conf` and restart CosmicWin to get the scene as a small
ambient indicator instead of a wallpaper. In this mode CosmicWin starts no wallpaper host and plays
no video, so your desktop background stays exactly as Windows has it.

```ini
wallpaper-mode = mini
mini-corner = top-right
```

- **Where.** A square window in one corner of the primary monitor (`mini-corner`: `top-left`,
  `top-right`, `bottom-left` or `bottom-right`, default `top-right`). Its side is one fifth of the
  monitor's height — 288 px on a 1440 px tall screen — and it is flush with the corner of the work
  area, so it stays clear of the taskbar and follows it if the taskbar moves or resizes.
- **Behavior.** Always on top, transparent, click-through and never focused: it does not take
  keyboard focus, does not appear in the taskbar or Alt+Tab, and mouse clicks go to whatever is
  underneath it.
- **Scene.** It draws a reduced variant of `wallpaper-scene` (no background, no starfield) at
  `wallpaper-fps`. The scene route above switches it live and saves the choice, exactly as in html
  mode.
- **Alerts** are shown inside the window, over the scene.
- **`Alt+M`** moves it to the next corner, clockwise (top-left, top-right, bottom-right,
  bottom-left, and around) and saves the new `mini-corner`. In any other wallpaper mode the chord
  does nothing.
- The tray menu's video pick and the video HTTP route do nothing in this mode; both write a
  `skipped reason=mini-mode` line to the desktop trace.

## Keybindings

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window |
| `Alt+Ctrl` + direction | Resize — grows toward a neighbour, shrinks when there is none |
| `Alt+[` / `Alt+]` | Ascend / descend scope, to move a whole group |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+Q` | Ask the focused window to close — it may refuse, and that is its right |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window there, without following it |
| `Alt+Shift+Q` | Close the desktop you are on — Windows hands its windows to a neighbour |
| `Alt+M` | Move the mini scene window to the next corner (only with `wallpaper-mode = mini`) |

Two collisions with Windows itself are worth knowing before you file a bug:

- On a layout with **AltGr** (Spanish, US-International), Windows reports the right Alt as
  `Ctrl+Alt`, so desktop chords answer only to the LEFT Alt — and `AltGr+arrow` is the resize chord.
- **`Alt+Shift` is Windows' default language-switch hotkey.** Every move chord starts with it, so the
  input language will flip unless you set that hotkey to *Not Assigned*.

## Prior art

Three projects were read while building this one. None is vendored, distributed, or needed to build,
run or test CosmicWin, and no code was copied from any of them.

- **[COSMIC](https://github.com/pop-os/cosmic-epoch)'s `cosmic-comp`** (GPL-3) — the tiling behaviour
  this project models: how a move walks the tree, how a resize splits its intent, and how a new
  window's split axis follows the tile it lands on. Rust to C#, so the algorithms were reimplemented
  by definition.
- **[WinMan](https://github.com/fancywm/winman)** by Veselin Karaganev (MIT) — the shape of the
  window, display and workspace abstractions. MIT is compatible with this project's licence.
- **WinMan.Windows** (GPL-2) — its enumerate-plus-hook window-tracking approach, reimplemented at
  roughly a fifth of the size and a different shape.

## Documentation

[`docs/notes.md`](docs/notes.md) — the things that were only discoverable by
getting them wrong once: how to run the tests and the false red that awaits if you set only one of
the two environment variables, why some diagnostics assert nothing on purpose, and how the
virtual-desktop interop defends itself against an undocumented vtable moving underneath it.

## Licence

MIT. See [LICENSE](LICENSE).

<p align="right">
  <a href="https://github.com/Gentleman-Programming/gentle-ai"><img src="https://raw.githubusercontent.com/Gentleman-Programming/gentle-ai/main/docs/assets/brand/built-with-gentle-ai.png" alt="Built with Gentle-AI" width="180"></a>
</p>
