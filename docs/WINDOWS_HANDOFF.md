# Windows handoff

This file is the continuation point for the first session on a Windows machine.

## Current verified state

Be precise about this, because most of the repository has never run.

**Verified on the authoring macOS machine (2026-08-25):**

- `Nocturne.Core` compiles clean with `TreatWarningsAsErrors` and the
  `latest-recommended` analyzer set.
- `Nocturne.Engine` compiles clean, including the whole libmpv P/Invoke surface.
- `Nocturne.Render` compiles clean, including the ANGLE, Direct3D 11, and mpv
  render-API interop. `EnableWindowsTargeting` makes this possible; it proves
  signatures, nullability, and analyzer conformance, and proves nothing about
  runtime behaviour.
- 67 tests pass: timecode formatting, seek clamping, snapshot reduction,
  playlist and shuffle semantics, subtitle sidecar matching.
- One real defect was found and fixed by those tests: `Timecode.FromSeconds`
  overflowed on an absurd duration because it constructed the `TimeSpan` before
  clamping it.

**Never executed anywhere:**

- Every P/Invoke in `Nocturne.Engine` and `Nocturne.Render`. No libmpv call has
  been made by this code.
- The entire render pipeline. No frame has been drawn.

**Verified in CI on `windows-latest` (run `32820878154`):**

- The full solution, `Nocturne.App` included, builds with 0 warnings and 0
  errors. The XAML compiler has been over every file, so resource-key typos and
  `x:Bind` type mismatches at compile time are behind us.
- `dotnet publish` produces a self-contained x64 app with `libmpv-2.dll` staged
  beside it, and Inno Setup packages an installer.

**Reviewed, 2026-08-25:** two independent passes over the interop layer and the
app layer, both told the code had never run. Thirteen defects found and fixed;
see `CHANGELOG.md`. Four of them would have ended the first session before it
produced any information — most usefully, the render failure message no longer
gets overwritten by ordinary playback snapshots, so a machine without ANGLE now
says so on screen instead of appearing to open and do nothing.

That review is not a substitute for running the thing. It narrows what the first
session has to discover; it does not tell you the pipeline works.

**Never launched:**

- Nobody has run the executable. Everything that only fails at runtime is ahead:
  resource resolution at load, whether `x:Bind` works against a `Window` root at
  all, and whether bindings assigned after `InitializeComponent` ever update —
  `MainWindow` assigns `ViewModel` after that call, and if the surface comes up
  blank, calling `Bindings.Update()` at the end of the constructor is the first
  thing to try.

No claim is made that the app launches, plays a file, or draws anything.

## Prerequisites

- Windows 11 (or Windows 10 21H2+) on x64.
- Visual Studio 2022 17.10+ with **.NET desktop development** and **Windows
  application development** workloads, or the .NET SDK plus the Windows SDK.
- .NET SDK per `global.json`.
- 7-Zip on `PATH`, for `fetch-mpv.ps1`.
- **ANGLE binaries.** See [`RENDERING.md`](RENDERING.md#risk-1--angle-is-not-distributed-with-libmpv).
  This is the blocking prerequisite and the reason step 1 below exists.

## Step 1 — the spike, before anything else

Do not start feature work. Do not fix XAML warnings. Prove the pipeline first,
because the entire architecture rests on one unverified assumption: that an
obtainable ANGLE build can wrap an app-owned D3D11 texture as an EGL surface
that libmpv will render into.

If that is false, the design changes, and every hour spent elsewhere first is
an hour spent on the wrong design.

```powershell
git clone <repository-url>
cd Nocturne
dotnet --info

# 1a. Native runtime. Expect exit code 2 and a warning about ANGLE.
./scripts/fetch-mpv.ps1 -Architecture x64

# 1b. Obtain libEGL.dll and libGLESv2.dll, then place them.
./scripts/fetch-mpv.ps1 -Architecture x64 -AnglePath <folder>

# 1c. Build.
dotnet restore Nocturne.sln -p:Platform=x64
dotnet build Nocturne.sln -c Debug -p:Platform=x64
```

Then run and check, in this order. Each line is a distinct thing that can fail.

- [ ] The window opens at all. A WinUI resource or binding error shows as a
  silent process exit; `App.OnUnhandledException` writes the exception to the
  debug output first.
- [ ] `AngleContext.Create` succeeds. Failure names the EGL error. The likely
  ones: `EGL_BAD_PARAMETER` from `eglGetPlatformDisplayEXT` means this ANGLE
  build does not accept an external D3D11 device.
- [ ] `eglCreatePbufferFromClientBuffer` succeeds. Failure here means the build
  lacks `EGL_ANGLE_d3d_texture_client_buffer`, which is **Risk 1 realised** —
  stop and re-read `RENDERING.md` §4 before writing code.
- [ ] `mpv_render_context_create` returns 0. A negative result usually means
  `vo=libmpv` did not take, which would mean `EngineOptions` was applied after
  `mpv_initialize` rather than before.
- [ ] Open a file. Any file. A single drawn frame retires the whole risk.
- [ ] The picture is the right way up. If not, flip `flipY` in
  `VideoRenderer.RenderFrame` — one character, and it is expected to be wrong
  about half the time.
- [ ] The transport bar composes **over** the video with visible translucency.
  If the video covers it, `ISwapChainPanelNative::SetSwapChain` did not take
  effect and the panel is showing its own background instead.

Record the outcome in `CHANGELOG.md` and update the state table in `README.md`.
Neither should keep claiming "never executed" after this session.

## Step 2 — playback smoke test

Only after step 1 draws a frame.

- [ ] Play H.264 1080p, HEVC 10-bit 4K, AV1, VP9, and a ProRes file.
- [ ] Confirm hardware decoding is actually on: `d3d11va` should keep CPU near
  idle during 4K playback. Steady CPU means it silently fell back to software,
  most likely because `hwdec` was set after initialization.
- [ ] Play a 23.976 fps file on a 60 Hz display for two minutes and watch for
  judder in a slow horizontal pan. This is what `video-sync=display-resample`
  and `mpv_render_context_report_swap` exist for; judder means the swap reports
  are not reaching libmpv.
- [ ] Seek repeatedly in a 4K HEVC file. Under 200 ms is the target.
- [ ] Resize the window continuously while playing. `ApplyPendingResize` releases
  the EGL surface and the texture before `ResizeBuffers`; a device-removed error
  here means something still held a back-buffer reference.
- [ ] Drag the window between a 100% and a 150% display. `CompositionScaleX`
  should keep the render surface at physical resolution; a soft picture means
  DIPs leaked into the size calculation.
- [ ] Open a file whose path contains Vietnamese diacritics, and one containing
  `#` and `%`. All strings cross the boundary as explicit UTF-8; a failure here
  would mean something reverted to default marshalling.
- [ ] Open a corrupt file and a file with no video stream. Both must show the
  error overlay and leave the app usable.
- [ ] Close the window during playback. Clean exit, no hang. `MpvClient.Dispose`
  bounds its join at two seconds and `VideoRenderer.Dispose` at two more.

## Step 3 — interface

- [ ] Compare against `UI_SPEC.md` §3 at 100%, 150%, and 200% display scale.
- [ ] Confirm timecodes do not jitter as they count. If they do,
  `FontFeatures="tnum"` is not reaching the fallback font.
- [ ] Confirm the seek thumb is absent at rest and appears on hover.
- [ ] Drag the seek bar during playback: the thumb must follow the pointer and
  must not snap back. That is what `BeginScrub`/`EndScrub` guard.
- [ ] Tab through the transport with the keyboard. Every control has an
  `AutomationProperties.Name`; confirm focus is visible against video.
- [ ] Space, Left, Right, F11, and Escape.
- [ ] Drop a file onto the window, and drop a folder (which should be rejected
  rather than crash).

## Known gaps, deliberately

These are not defects to fix on sight; they are unbuilt milestones.

- No application icon. `ApplicationIcon` is commented out in the csproj because
  pointing at a missing file fails the build.
- Subtitle and audio track selection: the toolbar button exists and does nothing.
- No settings persistence; `EngineOptions.Default` is compiled in.
- No HDR. See `RENDERING.md` Risk 3.
- No fullscreen independent-flip path. See `RENDERING.md` Risk 4.

## Do not do yet

- Do not add features before step 1 passes.
- Do not silence an EGL or D3D error to get past it — those messages are the
  only diagnostic the pipeline has.
- Do not move `Core` or `Engine` to `net8.0-windows` for convenience. The moment
  either names a Windows type, the test suite stops running off Windows and the
  state machine goes back to being verifiable only by launching the app.
- Do not mark a milestone complete in `PLAN.md` without recorded evidence from a
  real Windows run.
