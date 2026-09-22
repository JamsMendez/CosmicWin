# PlaybackSpike

Throwaway measurement: does a native `IMFMediaEngine` video loop (`SetLoop(true)`)
have a visible seam/gap at the loop point, or is it clean. Plays in an ordinary
top-level window — not attached to Progman, fully decoupled from AttachSpike.

## Run

```
cd spikes/PlaybackSpike
dotnet run -- "C:\path\to\video.mp4"
```

Requires a video file path as the only argument (H.264 MP4 is the safest bet on
stock Windows 11; HEVC/VP9/AV1 need Store codec extensions).

## What to look for

- Console log: `IMFMediaEngine created successfully`, `HasVideo()=...`,
  `GetNativeVideoSize=...`, then a timestamped line every time the media
  reaches the end and loops (`MF_MEDIA_ENGINE_EVENT_ENDED`).
- Watch the 1280x720 window across several loop points for a stutter, black
  frame, or freeze. The console timestamps plus what you saw on screen is the
  seam measurement — there is no automated pass/fail here.

Close the window to exit.

## Interop notes

CsWin32 (0.3.321) generates fully usable managed bindings for
`IMFMediaEngine`, `IMFMediaEngineNotify`, `IMFMediaEngineClassFactory`,
`IMFAttributes`, `MFStartup`/`MFCreateAttributes` — no hand-rolled COM interop
was needed. These are exposed as ordinary managed COM interfaces (not raw
vtable pointers): call methods directly, `out` parameters instead of `T*`,
and `IMFMediaEngineNotify` can be implemented as a plain C# class (a CCW is
created automatically when it's passed to `IMFAttributes.SetUnknown`).

The one thing CsWin32 does *not* generate is an activation path for the
`MFMediaEngineClassFactory` coclass, so the engine is created with
`Type.GetTypeFromCLSID` + `Activator.CreateInstance`, using
`CLSID_MFMediaEngineClassFactory` (`b44392da-499b-446b-a4cb-005fead0e6d5`,
from `mfmediaengine.h`, cross-checked against Microsoft Learn and the
wine-mirror IDL — not guessed).
