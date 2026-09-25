# Video endpoint follow-ups

## Objective

Fix the three pre-existing problems found while building the video wallpaper HTTP endpoint
(`odd/tasks/video-wallpaper-http-endpoint.md`, V5 notes). The maintainer authorized them on
2026-09-25, right after merging that feature into local main (`f03b7f4`).

## Problems

1. **The desktop trace drops lines.** `FileDesktopTrace.Record` uses `File.AppendAllText` and
   swallows every `IOException`. When another process holds `desktop-trace.log` open without write
   sharing, the append fails and the line is lost with no sign. `File.ReadAllLines`, many editors
   and `Get-Content` without `-Wait` do this. Seen in V5: a harness that polled the trace every
   50 ms lost the line of a successful 6.6 GB switch.
2. **The video wallpaper thread swallows exceptions untraced.** `MtaActionThread.Run` (private,
   nested in `AppComposition`) runs posted work in `try { work(); } catch { }`. A work item that
   throws after the import (persist, attach, play) leaves no trace line at all, and the wallpaper
   may already be stopped.
3. **`HttpAlertCommandServer` is misnamed.** Since V3 it serves `/v1/alerts` AND
   `/v1/wallpaper/video`.

## Approach

- P1: a bounded retry on a sharing violation. Up to 5 attempts, a few milliseconds apart (under
  ~50 ms in total). The line is dropped only if every attempt fails, as today. The writer's own
  share mode cannot help: a reader that denies writers blocks every writer. Retrying is the smallest
  fix that keeps `Record` synchronous. A background writer queue was rejected: more moving parts,
  and lines could be lost at exit. Keep the lock; `Record` must still never throw.
- P2: the loop's catch records the exception TYPE through the desktop trace (never the message,
  which can hold an absolute path), and the thread keeps running. Make the class testable
  (`internal`, or extract it) with an injected sink.
- P3: rename the class, its test class, the files, and the `createHttpAlertCommandServer` factory
  seam. Trace and diagnostic text (`alert-http ...`, `alert http: ...`) stays unchanged, because
  people and tests read it. `IAlertCommandServer` is shared with the named pipe server: out of scope.

## Tasks

- [ ] F1 `FileDesktopTrace`: bounded retry on a sharing violation. RED first: a test that holds the
  file open with `FileShare.Read`, releases it after ~10 ms on another thread, and expects the line
  on disk. Plus a test that a permanent hold still never throws and gives up within the bound.
- [ ] F2 `MtaActionThread`: trace the exception type from a failed posted work item; the loop keeps
  serving. RED first.
- [ ] F3 Rename `HttpAlertCommandServer` (name to be confirmed by the maintainer). Pure refactor:
  the suites are the guard.

## Constraints

- Everything stays local: no push, no gh.
- Strict TDD (runner `dotnet test`). Tests go with each work unit.
- No absolute paths in any trace line.

## Progress

- Branch `fix/video-endpoint-followups` from local main `f03b7f4`. Baseline: Layout 198, Alert 13,
  Interop 387/42 skipped, App 991/6 skipped; build has only the 3 pre-existing warnings.
- Next: F1.
