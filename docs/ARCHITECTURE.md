# Architecture

## 1. Layers

```text
┌──────────────────────────── Nocturne.exe ─────────────────────────────┐
│                                                                        │
│  Nocturne.App          Nocturne.Render        Nocturne.Engine          │
│  ─────────────         ───────────────        ──────────────           │
│  WinUI windows   ───▶  D3D11 + ANGLE    ◀───  libmpv interop           │
│  design system         swap chain             event pump               │
│  view models           mpv render ctx         snapshot reducer         │
│         │                     │                      │                 │
│         └─────────────────────┴──────────┬───────────┘                 │
│                                          ▼                             │
│                                  Nocturne.Core                         │
│                                  ──────────────                        │
│                                  playback state, timecodes,            │
│                                  playlist, format tables               │
└────────────────────────────────────────────────────────────────────────┘
```

Dependencies point inward and never outward:

- `App` depends on `Render`, `Engine`, and `Core`.
- `Render` depends on `Engine` (for the render-parameter structs) and `Core`.
- `Engine` depends on `Core` only.
- `Core` depends on nothing.

Two rules make the rest of the design hold:

**`Core` and `Engine` name no Windows type.** Both target `net8.0`, not
`net8.0-windows`. This is enforced by the compiler, not by convention: adding a
`using Microsoft.UI.Xaml` to `Engine` fails the build. The payoff is that the
playback state machine — the part with the subtle bugs — is unit-tested on any
machine, in milliseconds, without a window.

**No layer above `Render` names a graphics type.** `MainWindow` receives an
`IDXGISwapChain1` and hands it to a panel; it never creates one, never resizes
buffers, and never touches a device context.

## 2. Project responsibilities

### Nocturne.Core

Owns the vocabulary the app reasons in.

- `PlaybackSnapshot` — the whole playback state as one immutable value.
- `SeekMath` — every seek gesture funnels through it, so clamping is uniform.
- `Timecode` — formatting the elapsed/total pair as a matched, fixed-width unit.
- `PlaylistModel` — visible order and play order kept apart, so shuffle does not
  destroy the arrangement the user made.
- `MediaFormats`, `SubtitleSidecarMatcher` — what the app claims it can open, and
  which sidecar files belong to a given media file.

Nothing here does I/O. `SubtitleSidecarMatcher` takes a list of file names rather
than a directory, which is what lets the caller decide how enumeration happens —
relevant on a network share where enumeration can stall.

### Nocturne.Engine

Owns libmpv.

- `MpvNative` — the raw entry points, all `internal`, all pointer-typed.
- `MpvClient` — handle lifetime, UTF-8 string ownership, error translation, and
  the event pump. A reader/writer lock over the handle means disposal waits for
  in-flight calls instead of freeing underneath them.
- `PlaybackSnapshotReducer` — pure functions folding libmpv property changes into
  a `PlaybackSnapshot`. This is the seam that makes engine behaviour testable.
- `PlayerEngine` — the façade. Owns a client, observes the properties the UI
  reflects, and publishes whole snapshots.
- `EngineOptions` — the picture-quality specification, written as the libmpv
  options that produce it.

The reducer exists because libmpv reports one property at a time, out of order,
on its own thread. A `duration` for the next file can arrive before the `path`
that produced it. Reducing into a snapshot and publishing it whole means the UI
can never render a frame that mixes two files.

### Nocturne.Render

Owns the GPU. Documented separately in [`RENDERING.md`](RENDERING.md), because
it is the part of the system where the design decisions actually live.

### Nocturne.App

Owns the window.

- `Design/Palette.xaml`, `Design/Controls.xaml` — the design system, documented
  in [`UI_SPEC.md`](UI_SPEC.md).
- `PlayerViewModel` — the single place where the engine thread crosses to the UI
  thread, and where a scrub gesture suppresses incoming position updates.
- `MainWindow` — input, window chrome, and the join between the swap chain and
  the `SwapChainPanel`. Holds no playback state.

## 3. Threading

Three threads, listed in [`RENDERING.md` §5](RENDERING.md#5-threading) with the
rules that govern them. The one crossing that matters here:
`PlayerEngine.SnapshotChanged` is raised on the mpv event pump, and
`PlayerViewModel.OnSnapshotChanged` is the only subscriber that marshals. Every
consumer downstream of the view model may assume the UI thread.

## 4. What is deliberately absent

- **No dependency injection container.** Four classes are composed by hand in one
  constructor. A container would add indirection to a graph that fits on a
  screen.
- **No plugin host.** Lumen has one; it is a Milestone 6 question at the
  earliest, and a scripting surface designed before the app has a stable internal
  vocabulary would fossilise the wrong one.
- **No library database.** The playlist is the containing folder. A media library
  is a different product decision, not an incremental feature.
- **No settings persistence yet.** `EngineOptions` is a record with defaults;
  loading it from disk is Milestone 3.
