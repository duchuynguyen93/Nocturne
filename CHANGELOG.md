# Changelog

All notable changes to Nocturne are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The
project will follow [Semantic Versioning](https://semver.org/) from the first
public preview; during pre-alpha, breaking changes appear under `Unreleased`.

Entries state what was verified and what was not. "Compiles" is not "works", and
this file is where that distinction is kept honest.

## [Unreleased]

### Added

- Initial repository: four projects with an enforced dependency direction, and a
  CI pipeline that produces a downloadable Windows installer.

- **`Nocturne.Core`** — the vocabulary the app reasons in, platform-neutral and
  fully tested. `PlaybackSnapshot` carries the whole playback state as one
  immutable value; `SeekMath` centralises seek clamping so no call site computes
  `position + 10s` for itself; `Timecode` formats the elapsed/total pair as a
  matched fixed-width unit; `PlaylistModel` keeps the user's visible order and
  the shuffled play order apart so turning shuffle off restores the arrangement;
  `SubtitleSidecarMatcher` attaches `movie.srt` and `movie.vi.srt` to `movie.mkv`
  while rejecting `movie-copy.srt` and `movie.2024.remux.srt`.

- **`Nocturne.Engine`** — libmpv interop, also platform-neutral, so the state
  machine over it is testable without Windows. `MpvClient` owns the handle behind
  a reader/writer lock, so disposal waits for in-flight calls rather than freeing
  the handle underneath them, and passes every string as explicit UTF-8 rather
  than through the default marshaller, which is ANSI on Windows and would fail on
  the first accented path. `PlaybackSnapshotReducer` folds libmpv's one-property-
  at-a-time changes into whole snapshots, which is what stops the transport bar
  from ever showing one file's name beside another's duration. `EngineOptions`
  is the picture-quality specification written as the options that produce it —
  `ewa_lanczossharp` scaling, debanding, `display-resample` timing, D3D11VA
  hardware decoding — with a reason recorded per line.

- **`Nocturne.Render`** — the Direct3D 11, ANGLE, and mpv render-API pipeline.
  libmpv renders through ANGLE into a texture the app owns, which is copied into
  a composition swap chain and presented, with no pixel crossing into system
  memory. The swap chain is created for composition rather than for a window,
  which is what lets XAML compose a translucent transport bar over the video —
  the thing a `--wid` child window makes impossible. ANGLE is initialized on the
  app's own D3D11 device so the frame path carries no cross-device
  synchronisation.

- **`Nocturne.App`** — WinUI 3 shell. The design system reproduces Lumen
  Player's tokens, read from the computed styles on their site: the `ink` neutral
  ramp from `#0A0A0B` to `#F8F8FA`, the amber `#F5A623` accent, the radius scale,
  and the type roles. The transport sits on a gradient scrim rather than a filled
  strip so it stays legible over a bright scene without banding a dark one; the
  play button is the only filled amber shape on the surface; the seek thumb
  appears only on hover, so at rest the bar is a progress line rather than a
  control.

- Documentation set: `README.md`, `PLAN.md`, `docs/ARCHITECTURE.md`,
  `docs/RENDERING.md`, `docs/UI_SPEC.md`, `docs/DEVELOPMENT.md`,
  `docs/WINDOWS_HANDOFF.md`, `docs/LICENSING.md`, and three ADRs.

### Fixed

- `Timecode.FromSeconds` threw `OverflowException` on an absurd duration. It
  clamped after constructing the `TimeSpan`, and `TimeSpan.FromSeconds` throws
  for magnitudes it cannot represent — so the clamp never got the chance to run.
  Now clamped in double space first. Found by the test written for it, on the
  first run, against a damaged-container value of `1e12` seconds.

### Verified

On the authoring macOS machine, 2026-08-25:

- `Nocturne.Core`, `Nocturne.Engine`, and `Nocturne.Render` all compile with zero
  warnings under `TreatWarningsAsErrors` and the `latest-recommended` analyzer
  set. `Nocturne.Render` compiles off Windows via `EnableWindowsTargeting`, which
  checks interop signatures, nullability, and analyzer conformance — and nothing
  about runtime behaviour.
- 67 tests pass.

### Not verified

Stated explicitly so no later reader assumes otherwise:

- **No libmpv call has ever been made by this code.** Every P/Invoke in `Engine`
  and `Render` is unexecuted.
- **No frame has been drawn.** The render pipeline has never run.
- **`Nocturne.App` has never been built.** WinUI needs a Windows toolchain, so
  the XAML has not been through the XAML compiler; binding errors, resource-key
  typos, and `x:Bind` type mismatches are all still ahead.
- The ANGLE dependency is unconfirmed. mpv removed its ANGLE backend in 0.37, so
  the binaries must be sourced separately, and whether an obtainable build
  exposes `EGL_ANGLE_d3d_texture_client_buffer` on an external D3D11 device is
  the project's largest open question. See `docs/RENDERING.md` Risk 1.

### Known gaps

- No application icon; `ApplicationIcon` is commented out because pointing at a
  missing file fails the build.
- Subtitle and audio track selection are not implemented — the toolbar button is
  present and inert.
- `SubtitleSidecarMatcher` is written and tested but nothing calls it yet.
- No settings persistence, no HDR, no fullscreen independent-flip path.
- Nocturne's own code carries no licence yet. See `docs/LICENSING.md`.
