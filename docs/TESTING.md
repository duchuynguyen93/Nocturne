# Testing

What is checked automatically, what cannot be, and what a person has to look at.

## The shape of the problem

A video player is four layers deep in things that cannot be asserted on a build
server: a GPU, a display, a compositor, a driver. The response is not to give up
on automation but to push as much logic as possible into places that need none of
them — which is why `Nocturne.Core` and `Nocturne.Engine` target `net8.0` rather
than `net8.0-windows`, and why their tests run on Linux.

Four tiers, in descending order of how much they are worth per minute spent:

| Tier | Needs | Where it runs |
| --- | --- | --- |
| 1. Pure logic | nothing | `tests/Nocturne.Core.Tests`, on any OS |
| 2. Real libmpv | libmpv, no GPU | not built yet — see below |
| 3. Native runtime | Windows | the `windows-build` CI job |
| 4. A person | eyes | the checklist at the end |

## Tier 1 — pure logic (107 tests)

Everything the player decides before a pixel is involved.

| Area | What is pinned |
| --- | --- |
| `SeekMath` | Seeking past the end lands on `duration − 250ms`, not at the end, because libmpv treats a seek to the end as end-of-file and skips the file. Seeking before the start lands at zero. A zero duration means *unknown*, not *empty*, so the target passes through unclamped. |
| `Timecode` | Both halves of the elapsed/total pair always have the same number of fields, including when the position is negative, when it exceeds the duration, and when the duration is unknown. Values are truncated, never rounded, so the last second of a file does not read as the next minute. Magnitudes beyond 99 hours clamp instead of throwing. |
| `PlaylistModel` | Turning shuffle on pins the current item to the front so it is not played twice; turning it off restores the order the user sees. |
| `PlaybackSnapshotReducer` | libmpv reports one property at a time; the snapshot never mixes one file's name with another's duration. Non-finite volumes and speeds are refused rather than stored. Reaching the end survives the pause that immediately follows it. Every observed property has a case — with no exception list. |
| `SubtitleSidecarMatcher` | `movie.srt` and `movie.vi.srt` attach to `movie.mkv`; `movie-copy.srt` and `movie.2024.remux.srt` do not. |
| `MediaFormats` | Classification is case-insensitive, uses only the last extension, and the video/audio/subtitle tables do not overlap. |

Two of these were written to fail first, and did: `Math.Clamp(NaN, 0, 100)` is
`NaN`, and a pause arriving after the end demoted the status.

## Tier 2 — real libmpv, no GPU (not built yet)

The interop resolver already probes `libmpv.so.2` on Linux, so a test project
loading a real libmpv would run on an ordinary Linux runner. It would have to
build its options by hand rather than from `EngineOptions.Default`, which sets
`vo=libmpv` and so needs a render context that does not exist there.

The case that makes this tier worth building: **playing a two-second clip to its
end and asserting the end is reported.** That is precisely the bug found in this
round by reading — `keep-open=yes` means libmpv never unloads the file, and
`MPV_EVENT_END_FILE` only arrives after an unload — and a test at this tier would
have caught it the day it was written instead of a year later.

## Tier 3 — the native runtime (in CI today)

The `windows-build` job loads every shipped native library by full path, in
dependency order, and fails the build if any will not load. This is not a
file-existence check; that one passed for three builds while the app could not
start, because `libGLESv2.dll` imports a `zlib1.dll` that was not being shipped.

Worth adding, and cheap, because the runner has WARP as a software Direct3D 11
device: build an ANGLE context on WARP, create an mpv render context on it, draw
one frame offscreen and assert the pixels are neither all black nor all white.
All-white is the signature of the HDR-as-sRGB bug that shipped, so that assertion
converts a class of released bug into a red build.

## Tier 4 — the checklist

Ten seconds each, in order. Anything below the line that fails is worth a log
from `%LOCALAPPDATA%\Nocturne\logs\`.

1. Open the app. A window appears within about two seconds.
2. Drag an `.mkv` onto it. The picture appears, **the right way up**.
3. The transport bar is translucent — the video is visible through it.
4. Drag a window corner around for five seconds while playing. No freeze, no
   black band, no crash.
5. Double-click the picture, then press Escape. The window comes back the size
   it was, including maximized if it was maximized.
6. Open an HDR file. It is a picture, not a white rectangle.
7. Drag the seek bar to the far right. It stops on the last frame and does not
   skip to the next file.
8. Click once on the volume track. The volume changes and stays changed.
9. Let a short clip in a folder of several play to its end. The next file starts.
10. Move the window to a display at a different scale. The picture stays sharp.
11. At 30 seconds in, press Previous — it restarts the file. Press it again
    within three seconds — it goes to the previous file.
12. Open a file on a network share, then disconnect it. An error appears and the
    app stays responsive.
