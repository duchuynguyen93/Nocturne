# Nocturne implementation plan

Last updated: 2026-08-25

The working execution plan. A milestone is complete only when its acceptance
criteria have been observed on real Windows hardware and recorded in
`CHANGELOG.md`. Compiling is not evidence.

## Status legend

- `[x]` implemented and verified
- `[~]` implemented but unverified — compiles, has never run
- `[ ]` not started
- `BLOCKED` cannot proceed until the stated condition clears

## Scope

### Product scope

A Windows desktop media player that:

- plays whatever FFmpeg plays, with hardware decoding;
- renders through libplacebo, with correct scaling, debanding, and frame timing;
- presents an interface that composes over the video rather than boxing it in;
- opens instantly, remembers nothing it does not need to, and reports nothing.

### Non-goals

- Cross-platform. `Core` and `Engine` stay portable because that makes them
  testable, not because a macOS build is planned.
- A media library or metadata database. The playlist is the containing folder.
- Encoding, transcoding, or editing.
- Streaming service integration. Plex and Jellyfin are open questions for after
  local playback is excellent, not before.
- Telemetry of any kind.

## Milestone 0 — Foundation

Goal: a repository that builds, tests, and produces a downloadable Windows
artifact.

- [x] Four-project layering with enforced dependency direction.
- [x] `Core` and `Engine` platform-neutral, verified by a Linux CI job.
- [x] Central package versions, deterministic builds, warnings as errors.
- [x] 67 tests covering timecodes, seek clamping, snapshot reduction, playlist
  semantics, and subtitle sidecar matching.
- [x] `Nocturne.Render` compiles off Windows via `EnableWindowsTargeting`.
- [x] CI: Linux tests, Windows build, self-contained publish, Inno Setup
  installer, formatting check.
- [ ] `Nocturne.App` compiles. **Never attempted** — needs a Windows toolchain.
- [ ] CI produces a green Windows build.
- [ ] Application icon.

Acceptance: a fresh clone restores, builds, and tests on Windows, and the
Actions run yields an installer.

## Milestone 1 — The spike BLOCKED on ANGLE binaries

Goal: prove the composition pipeline. Nothing else proceeds until this passes.

- [~] D3D11 device, composition swap chain, ANGLE context on the app's device.
- [~] mpv render context over the OpenGL render API.
- [~] Render thread with an update callback and deferred resize.
- [~] `SwapChainPanel` attachment through `ISwapChainPanelNative`.
- [ ] **One frame drawn on real hardware.**
- [ ] Picture the right way up.
- [ ] XAML composing over the video with visible translucency.
- [ ] Resize while playing without a device-removed error.

Acceptance: a file plays in the window with the transport bar translucent over
it. See `docs/WINDOWS_HANDOFF.md` step 1.

**Blocked because** ANGLE is no longer distributed with libmpv and must be
sourced separately. If no obtainable build exposes
`EGL_ANGLE_d3d_texture_client_buffer`, the architecture changes — see
`docs/RENDERING.md` Risk 1. This is the single most important open question in
the project.

## Milestone 2 — Playback

Goal: a player someone would actually use for an evening.

- [~] Open, play, pause, seek, volume, mute, speed.
- [~] Folder-as-playlist with next and previous.
- [~] Repeat and shuffle, with visible order kept separate from play order.
- [~] Error surface for unplayable files.
- [ ] Subtitle track selection, embedded and sidecar. The toolbar button exists
  and does nothing.
- [ ] Audio track selection.
- [ ] Sidecar subtitles attached automatically on open — `SubtitleSidecarMatcher`
  is written and tested but nothing calls it.
- [ ] Resume position per file.
- [ ] Frame stepping.

Acceptance: watch a full film, with subtitles, without touching another
application.

## Milestone 3 — Settings and quality

- [ ] Settings persisted to `%LOCALAPPDATA%\Nocturne`, with a schema version.
- [ ] Renderer presets exposing the `EngineOptions` choices.
- [ ] Exclusive-mode audio and bitstream passthrough, as an explicit opt-in.
- [ ] HDR passthrough. See `docs/RENDERING.md` Risk 3 — needs a 10-bit swap
  chain, `SetColorSpace1`, `SetHDRMetaData`, and a matching FBO internal format.
- [ ] Custom GLSL shader loading.

Acceptance: the checklist in `docs/WINDOWS_HANDOFF.md` step 2 passes on both an
SDR and an HDR display.

## Milestone 4 — Interface completion

- [ ] Application icon at every required size.
- [ ] Auto-hiding chrome in fullscreen, on a pointer-idle timer.
- [ ] Track and settings menus in the design language.
- [ ] Keyboard shortcut coverage and a shortcut reference.
- [ ] High contrast support and a Narrator pass.
- [ ] Verified at 100%, 150%, and 200% display scale.

## Milestone 5 — Performance

- [ ] Fullscreen independent-flip path. See `docs/RENDERING.md` Risk 4. Measure
  before building — the copy this design adds may not be worth removing.
- [ ] Startup time and first-frame latency budgets, with a recorded baseline.
- [ ] Idle CPU near zero during 4K playback, confirmed with a profiler rather
  than Task Manager.
- [ ] Memory ceiling under sustained playback and rapid seeking.

## Milestone 6 — Distribution

- [ ] Licence decision. See `docs/LICENSING.md` — this gates everything below.
- [ ] `THIRD_PARTY_NOTICES.md`.
- [ ] Code signing.
- [ ] Update check, off by default.
- [ ] ARM64 build.

## Open questions

1. **ANGLE.** Milestone 1's blocker. Everything downstream assumes it resolves.
2. **Licence.** GPL is the path of least resistance and forecloses a commercial
   release. Deciding late means re-testing every format against an LGPL build.
3. **Library or no library.** The current answer is no, and the design leans on
   it: the playlist is a folder listing. Reversing it later is a large change.
4. **Plugins.** Lumen exposes a JavaScript surface. Designing one before the
   app's own vocabulary is stable would fossilise the wrong one.
