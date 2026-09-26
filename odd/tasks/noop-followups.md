# Follow-ups of the same-video and alert busy-ignore features

## Objective

Close the three open review findings left by `same-video-noop.md` and `alert-busy-ignore.md`.

## Items

1. R3-inplace-edit-hardlink (same-video-noop): a video re-encoded or edited IN PLACE keeps its NTFS
   identity, so a repeat HTTP request is skipped as `unchanged` and never picks up the new content.
   DECIDED by the maintainer 2026-09-26: it must RELOAD.
2. R3-predicate-throw-not-contained (same-video-noop): an injected `isSameVideoFile` that throws
   escapes the video work item, while the comment claims any failure reads as "different".
3. R3-http-202-claim-unproved (alert-busy-ignore): the ignored alert's 202 is proven only through the
   pipe; no test drives the HTTP route.

## Approach

- Items 1 + 2 together. Comparing size/last-write of the requested path against the imported path
  CANNOT work: with a hard link both names are the same file, so they always agree. Instead take a
  SNAPSHOT (volume serial, file index, size, last-write time) of the file when playback starts
  successfully, and on an HTTP request compare a fresh snapshot of the REQUESTED path against it.
  Skip only when all four match and playback is active. The snapshot reader is wrapped so any
  exception reads as "no snapshot" = different = reload (item 2 disappears by construction).
- Item 3: a test that sends a second alert through the real HTTP route while one is showing and
  asserts 202 `ok` plus the `alert ignored` trace.

## Constraints

- Strict TDD, runner `dotnet test`. Mutation-check any test that passes on its first run.
- Decisions of the parent features stay: HTTP only for the video skip, same 202 response, tray
  re-pick always reloads, copy fallback reloads.
- Everything stays local: no push, no gh.

## Tasks

- [ ] F1 Video snapshot: replace the identity-only check with the playback-start snapshot; exception
  -> reload. Tests: in-place edit (same identity, new size or last-write) reloads; unchanged file
  still skips; throwing reader reloads; existing same-video tests adapted (not weakened).
- [ ] F2 HTTP-level test for an ignored alert (202 `ok`, trace `alert ignored`, never shown).
- [ ] F3 Hardware: rewrite the playing video in place (touch last-write) and resend -> reload;
  resend without change -> `unchanged`.

## Progress

- 2026-09-26: branch `fix/noop-followups` from main 001fcb3. Route: F1+F2 delegated to one writer.
