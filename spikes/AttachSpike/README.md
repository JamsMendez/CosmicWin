# AttachSpike

Throwaway measurement: does the Progman/WorkerW "wallpaper behind icons" trick still
work on this machine, and which code path (legacy vs. 24H2+ raised desktop) does it take.

## Run

```
cd spikes/AttachSpike
dotnet run
```

No arguments. It logs every step (handles, GetLastError, which path it took) to the
console, then shows a solid magenta window behind the desktop icons if it worked.

Press any key in the console to exit; the magenta window is destroyed with the process.

## What to look for

- Console line "raised desktop layout" vs "legacy desktop layout" — tells you which
  path this Windows build takes.
- A solid magenta rectangle behind your desktop icons, icons still visible and
  clickable, taskbar unaffected. If nothing appears, or icons disappear, or the
  taskbar breaks, check the console log for which step failed (`GetLastError`).
