<img src="docs/image/icon-256.png" alt="CosmicWin" width="120" align="right" />

# CosmicWin

A tiling window manager for Windows 11, modelled on COSMIC's tiling behaviour, with an animated
desktop scene and live build alerts.

- **Tiling** — windows are arranged in a tree and given the whole work area between them. Focus,
  movement, resizing and Windows' own virtual desktops are driven from the keyboard.
- **Wallpaper** — an animated HTML scene as the desktop wallpaper (the default), a small always-on-top
  scene window instead, or a looping MP4 video.
- **Alerts** — flash `warning` / `failed` tiles over the desktop from a terminal or over a
  localhost-only HTTP API, for example when a build breaks.

## Contents

- [Quick start](#quick-start)
- [Keybindings](#keybindings)
- [Tray menu](#tray-menu)
- [Recommended Windows setting](#recommended-windows-setting)
- [Tiling](#tiling)
- [Wallpaper](#wallpaper)
- [Alerts](#alerts)
- [Settings](#settings)
- [HTTP API](#http-api)
- [Start at logon](#start-at-logon)
- [Prior art](#prior-art) · [Documentation](#documentation) · [Licence](#licence)

## Quick start

Requirements:

- Windows 11 (developed against build 26200)
- .NET 10 SDK

```powershell
git clone <this repo>
cd CosmicWin
./scripts/run.ps1
```

`run.ps1` builds, copies the output to a git-ignored `run/`, and launches that copy elevated — accept
the UAC prompt. Running the copy leaves the build tree unlocked, so builds and tests keep working
while the app is open. Exit from the tray icon.

CosmicWin runs elevated: it manages windows belonging to other processes, and Windows will not allow
that from a normal process.

## Keybindings

Keybindings are compile-time; there is no file to remap them.

**Focus and layout**

| Chord | Action |
| --- | --- |
| `Alt` + `H`/`J`/`K`/`L` or arrows | Move focus |
| `Alt+Shift` + direction | Move the window |
| `Alt+Ctrl` + direction | Resize — grows toward a neighbour, shrinks when there is none |
| `Alt+[` / `Alt+]` | Ascend / descend scope, to move a whole group |
| `Alt+O` | Toggle the focused group's split axis |
| `Alt+Q` | Ask the focused window to close — it may refuse, and that is its right |

**Virtual desktops**

| Chord | Action |
| --- | --- |
| `Alt+1`..`Alt+9` | Go to that virtual desktop, creating desktops until it exists |
| `Alt+Shift+1`..`Alt+Shift+9` | Send the focused window there, without following it |
| `Alt+Shift+Q` | Close the desktop you are on — Windows hands its windows to a neighbour |

**CosmicWin itself**

| Chord | Action |
| --- | --- |
| `Alt+T` | Turn tiling on or off — the same switch as the tray's **Modo tiling**, saved to `tiling` |
| `Alt+M` | Move the mini scene window to its next position, clockwise (only with `wallpaper-mode = html-mini`) |

While tiling is off, the layout is left exactly where it was: focus and resize still work on the
windows' own rectangles, desktop chords and `Alt+Q` work as usual, and move, scope and split-axis
chords do nothing. `Alt+T` always works, so it turns tiling back on too.

Two collisions with Windows itself are worth knowing before you file a bug:

- On a layout with **AltGr** (Spanish, US-International), Windows reports the right Alt as
  `Ctrl+Alt`, so desktop chords answer only to the LEFT Alt — and `AltGr+arrow` is the resize chord.
- **`Alt+Shift` is Windows' default language-switch hotkey.** Every move chord starts with it, so the
  input language will flip unless you set that hotkey to *Not Assigned*.

## Tray menu

Right-click the CosmicWin icon in the notification area.

| Item | What it does |
| --- | --- |
| **Modo tiling** | Tiling on or off; the tick shows the current state. Same switch as `Alt+T`. |
| **Borde de foco** | Draw CosmicWin's own focus border, or not. |
| **Color del borde** | **Elegir...** picks a colour; **Acento del sistema** goes back to the accent colour. |
| **Wallpaper de video...** | Pick an MP4 for video mode — see [Video](#video). |
| **Pausar** / **Reanudar** | Stop, or restart, the keyboard hook: while paused no chord works and new windows are not tiled. |
| **Reload** | Re-read the window exception list and `gap` from `settings.conf`. |
| **Salir** | Exit CosmicWin. |

Tiling, the focus border and its colour are saved to `settings.conf` as you change them.

## Recommended Windows setting

Turn off **Settings → System → Multitasking → "Snap windows"**. Windows' own edge snapping competes
with the tree: dragging a window to an edge hands it half the screen behind CosmicWin's back, and the
snap layout flyout appears over a work area CosmicWin has already divided. With it off, a dragged
window does what CosmicWin says it does. The setting takes effect at your next sign-in.

CosmicWin never touches this setting itself — it is yours to set, and it stays set after CosmicWin
exits. Nothing depends on it either: the guards that keep a maximized or self-resizing window from
breaking the layout run the same whether Snap is on or off, because maximize also arrives from the
maximize button, a double click on the title bar, and `Win+Up`.

## Tiling

### What works

- **Tiling** — every window gets a share of the work area, with a configurable uniform gap
  (`gap`, default 8, 0–64 pixels). The same gap draws around and between an alert's tiles too.
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

### Not yet

- **One monitor.** The layout engine is monitor-aware and multi-monitor requirements exist, but
  nothing beyond a single display is exercised or claimed.
- **An emptied virtual desktop is not removed.** Deliberate — see the notes.

## Wallpaper

`wallpaper-mode` in `settings.conf` picks one of three modes. Changing it by hand needs a restart.

| Mode | What you get |
| --- | --- |
| `html` (default) | An animated scene as the desktop wallpaper. |
| `html-mini` | No wallpaper; the scene plays in a small always-on-top window instead. |
| `video` | A looping MP4 as the desktop wallpaper. |

### HTML scenes

Four scenes ship, chosen by `wallpaper-scene`: `processing` (the default), `explorer`, `idle` and
`raphael`. The scene is rendered through a permanently preloaded WebView2 layer rather than a browser
window. Changing `wallpaper-scene` by hand needs a restart; the [scene route](#post-v1wallpaperscene)
switches it live and saves it.

`wallpaper-fps` caps the scene's own frame rate at `30` or `60` (default `60`); before this setting
existed the scene drew uncapped, at the display's own refresh rate. In `raphael`, the gold glyph ring
carries three times as many characters as the standard ring rule gives, drawn as thin upright
strokes with no glow, in both the wallpaper and the mini window.

Alerts render through the same preloaded WebView2 layer as the scene itself: each `warning`/`failed`
tile's letters are see-through, showing the running scene's own animation moving inside the letter
shapes, rather than a flat colour overlay. Every scene has its own hook for this effect.

### Mini scene window

The scene as a small ambient indicator instead of a wallpaper. In this mode CosmicWin starts no
wallpaper host and plays no video, so your desktop background stays exactly as Windows has it.

<!-- Video de Wallpaper Mini: paste the GitHub user-attachments URL on the empty line below, alone on its line. -->


```ini
wallpaper-mode = html-mini
mini-position = top-right
```

- **Where.** A square window at one of 8 positions on the primary monitor, set by `mini-position`
  (see [Settings](#settings)). Its side is one fifth of the monitor's height — 288 px on a 1440 px
  tall screen — and it is flush with its corner or side of the work area, so it stays clear of the
  taskbar and follows it if the taskbar moves or resizes. `Alt+M` moves it clockwise to the next
  position (top-left, top-center, top-right, right-center, bottom-right, bottom-center,
  bottom-left, left-center, and around) and saves the new `mini-position`.
- **Behavior.** Always on top, transparent, click-through and never focused: it does not take
  keyboard focus, does not appear in the taskbar or Alt+Tab, and mouse clicks go to whatever is
  underneath it.
- **Scene.** It draws a reduced variant of `wallpaper-scene` at `wallpaper-fps`, with no background:
  - `idle`: the white ring disc with its alphabet, constellation and hieroglyph bands, the planet
    centered in the ring, and the chroma fan below it.
  - `explorer`: the same ring as `idle` under a semi-transparent blue layer that tints only the ring
    and planet, plus the rising sparks.
  - `processing`: the folding bands, orbit blocks, octagon, sphere, rays and core, scaled to the
    window, over a soft green nebula.
  - `raphael`: the blue and gold glyph rings, the gold polygon and the core with its rotating rays,
    over a gold nebula.

  Fine details (constellation dots, glyph strokes, polygon lines) are scaled to the window instead of
  keeping their wallpaper pixel size. A dark base sits under the rings and structure, so windows
  behind the mini window never read through it, while the gaps between rings stay see-through.
  Everything fades out before the window's edges, so its square shape never shows. The scene route
  switches it live and saves the choice, exactly as in html mode.
- **Alerts** are shown inside the window, over the scene, even while another app is fullscreen.
- The tray menu's video pick and the video HTTP route do nothing in this mode; both write a
  `skipped reason=mini-mode` line to the desktop trace.

### Video

Loops a single MP4 as the desktop wallpaper (`wallpaper-mode = video`).

The tray menu's **Wallpaper de video...** entry opens a file picker restricted to `.mp4` files. The
picked file is imported into `%LOCALAPPDATA%\CosmicWin\video-wallpaper<ext>` before it plays:

- **Same drive as `%LOCALAPPDATA%`** — the import is a hard link, so switching to a multi-gigabyte
  file is instant. A hard-linked import is the same data as the original, so editing the source file
  in place changes the wallpaper too.
- **Different drive, or linking fails** — CosmicWin falls back to copying the file, which can take
  minutes for a large video.

Either way the wallpaper keeps playing if you later move or delete the original. Re-picking
overwrites the previous import; there is only ever one active video, recorded at
`video-wallpaper-path` in `settings.conf`. The [video route](#post-v1wallpapervideo) does the same
switch from another program.

If Explorer restarts (a crash, or `explorer.exe /restart`), CosmicWin listens for the shell's own
`TaskbarCreated` broadcast and re-attaches the video host to the desktop automatically, without
losing the running video.

In html mode (the default) the video route answers 503 and never starts a video, and the tray menu's
video pick does nothing. Both write a `skipped reason=html-mode` line to the desktop trace.

## Alerts

CosmicWin can flash a `warning` or `failed` alert over the desktop, for example when a build breaks.
Alerts are on by default (`alerts = on`). Each command asks for 1–16 tiles per kind (16 in total)
and an optional duration of 1–60 seconds (default 5).

From a terminal, `CosmicWinAlert.exe` sends a command over a per-user named pipe:

```powershell
CosmicWinAlert.exe warning:2 failed:1 duration:5
```

Other programs can send the same command over HTTP — see
[`POST /v1/alerts`](#post-v1alerts).

## Settings

CosmicWin has no installer: the first time it runs, it writes `%LOCALAPPDATA%\CosmicWin\settings.conf`
itself, with every default already filled in. An existing file is never rewritten except by a
tray-menu change, `Alt+T` or `Alt+M`, a settings-changing HTTP request (the scene route), or a hand
edit of your own. A hand edit takes effect at CosmicWin's next start; **Reload** in the tray applies
`gap` without one.

| Key | Default | Meaning |
|---|---|---|
| `tiling` | `on` | Lay windows out at all; off leaves them where they open. |
| `gap` | `8` | Whole pixels of space around and between tiled windows and alert tiles (0–64). |
| `focus-border` | `on` | Draw CosmicWin's own thicker focus border. |
| `border-color` | `accent` | `#RRGGBB`, or `accent` to follow Windows' own accent colour. |
| `wallpaper-mode` | `html` | `html`, `html-mini` or `video` — see [Wallpaper](#wallpaper). |
| `wallpaper-scene` | `processing` | Which scene to show: `processing`, `explorer`, `idle` or `raphael`. |
| `wallpaper-fps` | `60` | Caps the scene's own frame rate: `30` or `60`. |
| `mini-position` | `top-right` | Where the mini scene window sits: corners `top-left`, `top-right`, `bottom-left`, `bottom-right`, or side midpoints `top-center`, `right-center`, `bottom-center`, `left-center`. |
| `video-wallpaper-path` | *(blank)* | Absolute path to the imported video wallpaper. Set by the tray menu or the video route, not meant to be hand-edited. |
| `alerts` | `on` | Accept live alert commands (named pipe and HTTP). |
| `http-server` | `on` | Run the loopback HTTP server. One switch for every route. |
| `http-server-port` | `47811` | The loopback TCP port the HTTP server listens on. |

### Migrating from older settings

Older files keep working: CosmicWin still reads the old names, and the next save rewrites the file
with only the new ones. When a file has both an old key and its replacement, the new key wins.

| Old | New |
|---|---|
| `alerts-enabled` | `alerts` |
| `alert-http` | `http-server` |
| `alert-http-port` | `http-server-port` |
| `mini-corner` | `mini-position` |
| `wallpaper-mode = mini` | `wallpaper-mode = html-mini` |
| `video-wallpaper-http`, `wallpaper-scene-http` | removed: ignored, the routes follow `http-server` |

## HTTP API

Other programs on the same PC can drive CosmicWin over HTTP. One local server serves every route,
on one port and with one bearer token.

| Route | Body | Does |
|---|---|---|
| [`POST /v1/alerts`](#post-v1alerts) | `{"warning":2,"failed":1,"duration":5}` | Show an alert |
| [`POST /v1/wallpaper/scene`](#post-v1wallpaperscene) | `{"scene":"idle"}` | Switch the HTML scene live |
| [`POST /v1/wallpaper/video`](#post-v1wallpapervideo) | `{"path":"D:\\Videos\\space.mp4"}` | Switch the video wallpaper |

### Access

- **On by default** (`http-server = on`), so the port is already open on a fresh install. To close
  it, set `http-server = off` in `settings.conf` and restart CosmicWin.
- **Loopback only** — `127.0.0.1` / `localhost`, never reachable over the network.
- **Bearer token** — on first start CosmicWin writes a random token to
  `%LOCALAPPDATA%\CosmicWin\alert-http.token` (the name predates the other routes), kept across
  restarts. Delete the file to get a new one on the next start.
- **Port in use** — the whole HTTP server stays off, and the named pipe keeps working for alerts.
  The reason is written to CosmicWin's desktop trace.

Every request to every route is checked in the same fixed order, cheapest first: the connection must
be loopback; there must be no `Origin` header (any value means a browser sent it); the `Host` header
must be exactly `127.0.0.1:<port>` or `localhost:<port>` (blocking DNS rebinding); the path must
name a route that is turned on; the method must be `POST`; the bearer token must match; and
`Content-Type` must be `application/json` — only then is the body itself parsed and validated. Each
route then keeps its own mode guard.

Status codes shared by every route:

| Status | Meaning |
|---|---|
| 401 | Missing or wrong bearer token |
| 403 | Request from a browser (`Origin` header), a foreign `Host`, or not from this PC |
| 404 / 405 | An unknown path, a route that is turned off, or any method other than `POST` |
| 415 | `Content-Type` is not `application/json` |

### Calling a route

PowerShell:

```powershell
$token = Get-Content "$env:LOCALAPPDATA\CosmicWin\alert-http.token"
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:47811/v1/alerts `
  -Headers @{ Authorization = "Bearer $token" } `
  -ContentType 'application/json' -Body '{ "warning": 2, "failed": 1, "duration": 5 }'
```

curl:

```sh
curl -X POST http://127.0.0.1:47811/v1/alerts \
  -H "Authorization: Bearer $(cat "$LOCALAPPDATA/CosmicWin/alert-http.token")" \
  -H "Content-Type: application/json" \
  -d '{"warning":1}'
```

For the other routes, change the path and the body, for example
`$body = @{ scene = 'idle' } | ConvertTo-Json` or `-d '{"path":"D:\\Videos\\space.mp4"}'`.

### POST /v1/alerts

The body is a JSON object with `warning`, `failed` and `duration`, all integers and all optional.
At least one of `warning` or `failed` is required. The request is checked by the same rules as the
pipe — see [Alerts](#alerts).

| Status | Meaning |
|---|---|
| 202 | Queued; body `ok` |
| 400 | Bad JSON, unknown field, or a command the alert rules reject |
| 413 | Body larger than 1 KB |
| 503 | Alerts are turned off |

### POST /v1/wallpaper/scene

The body is a JSON object with one field, `scene`: one of `processing`, `explorer`, `idle` or
`raphael`, in any letter case. It works in `html` and `html-mini` mode.

The request is answered as soon as the name is checked; the switch then runs on the UI thread and
the new scene is saved to `wallpaper-scene`, so it survives a restart. Asking for the scene already
showing is accepted and changes nothing. If the switch fails, the current scene stays and nothing is
saved; the failure is written to the desktop trace.

| Status | Meaning |
|---|---|
| 202 | Accepted; the switch runs on the UI thread. Body `ok` |
| 400 | Bad JSON, unknown field, or a scene name that is not one of the four |
| 413 | Body larger than 256 bytes |
| 503 | Not in html or html-mini mode (`wallpaper-mode = video`), or this CosmicWin has no scene page or mini window to switch |

### POST /v1/wallpaper/video

The body is a JSON object with one field, `path`: the absolute path of a video that is already on
this PC. It must start with a drive letter and a backslash (`C:\...`). Relative paths, forward
slashes, network shares, network drives, device paths and URLs are rejected. Nothing is ever
downloaded.

The request is answered as soon as the path is checked. The switch itself then runs in the
background, exactly like a pick from the tray menu — see [Video](#video) for the hard-link import and
what it guarantees. If the switch fails, the previous video keeps playing. The outcome is written to
the desktop trace, never the path.

| Status | Meaning |
|---|---|
| 202 | Accepted; the switch runs in the background. Body `ok` |
| 400 | Bad JSON, unknown field, or a path that is not an absolute local drive path |
| 404 | The file does not exist, or the route is turned off (the body tells which) |
| 413 | Body larger than 4 KB |
| 415 | The file is not an `.mp4`, or `Content-Type` is not `application/json` (the body tells which) |
| 503 | This CosmicWin has no video wallpaper to switch, or `wallpaper-mode = html` is on (the default) |

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
