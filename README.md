<img src="docs/image/icon-256.png" alt="CosmicWin" width="120" align="right" />

# CosmicWin

A tiling window manager for Windows 11, modelled on COSMIC's tiling behaviour.

Windows are arranged in a tree and given the whole work area between them. Focus, movement, resizing
and Windows' own virtual desktops are driven from the keyboard.

## What works

- **Tiling** — every window gets a share of the work area, with a configurable uniform gap.
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
- **No configuration file.** Keybindings and the gap are compile-time.

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

Other programs on the same PC can send the same command over HTTP. The endpoint is **off by
default**. Turn it on in `settings.conf` and restart CosmicWin:

```ini
alert-http = on
alert-http-port = 47811
```

On first start CosmicWin writes a random token to `%LOCALAPPDATA%\CosmicWin\alert-http.token`. It
keeps that token across restarts. Delete the file to get a new one on the next start.

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

| Status | Meaning |
|---|---|
| 202 | Queued; body `ok` |
| 400 | Bad JSON, unknown field, or a command the alert rules reject |
| 401 | Missing or wrong bearer token |
| 403 | Request from a browser (`Origin` header), a foreign `Host`, or not from this PC |
| 404 / 405 | Any path other than `/v1/alerts`, or any method other than `POST` |
| 413 | Body larger than 1 KB |
| 415 | `Content-Type` is not `application/json` |
| 429 | The alert queue is full |
| 503 | Alerts are turned off |

The endpoint listens on `127.0.0.1` and `localhost` only. It refuses connections from other
machines, and it refuses web pages even when they run on your own PC. If the port is already in
use, the HTTP endpoint stays off and the named pipe keeps working. The reason is written to
CosmicWin's desktop trace.

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
