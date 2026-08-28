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

- **The picture is the right way up.** The first frame this project ever rendered
  was upside down. `MPV_RENDER_PARAM_FLIP_Y` was set to 1 on the reasoning that
  GL's origin is bottom-left and Direct3D's is top-left — but the surface is a
  pbuffer wrapping a D3D11 texture, and ANGLE's Direct3D backend already inverts
  the viewport when it translates GL. The correction was being applied twice.

- **The player can load its renderer.** `libGLESv2.dll` imports `zlib1.dll`,
  which is an MSYS2 library and is not present on Windows, and it was never
  shipped. `LoadLibrary` failed on it every time.

  It presented as a launch crash rather than as a missing dependency because
  `libEGL.dll` is a 260 KB forwarding shim that does not import `libGLESv2.dll`
  at all — it loads it at the first EGL call. So `libEGL` loaded cleanly, the
  installer's file checks all passed, and the process died inside
  `eglQueryString` with no exception and nothing in any log.

  `zlib1.dll` is now fetched and shipped, which closes the chain: its own imports
  are `KERNEL32` and `msvcrt` and nothing else.

  The check that would have caught this on the first build is now in CI. It does
  not test that the files exist — that test passed for three builds while the app
  could not start. It **loads every shipped library**, in dependency order, on
  the runner. `LoadLibrary` resolves the whole import table, which is the step
  that was failing, and it needs no GPU.

- Preflight reports the Win32 error rather than a bare false. `NativeLibrary.TryLoad`
  answers only yes or no, and "no" covers a missing file, a wrong architecture and
  an unsatisfied dependency — three different fixes. `126 ERROR_MOD_NOT_FOUND` on a
  file that demonstrably exists names the whole problem in one number.

- The log could not say which build wrote it, and could not say where inside the
  native runtime a run stopped. Both were costing rounds of guessing paid for by
  someone installing a build on a machine nobody here can reach.

  `NativePreflight` now runs before the pipeline is built. It names the graphics
  adapters through DXGI — which does not create a device, so it survives the case
  where device creation is itself the fault — then loads each native library **by
  full path, one at a time, with a log line on both sides**. A missing "loaded"
  line names the library whose entry point killed the process, which is not
  something the loader reports and not something any `catch` sees. Loading by
  full path also proves which copy loaded, rather than whichever same-named file
  `PATH` happened to offer.

  `D3D11CreateDevice` is logged immediately before the call for the same reason:
  it runs the display driver's own code inside this process.

  Startup now records the informational version, the executable's timestamp and
  its path. CI stamps the commit into the informational version.

- **The app force-closed on launch.** The previous build was the first in which
  `libEGL.dll` actually loaded — earlier ones failed at `LoadLibrary` with
  `ERROR_BAD_EXE_FORMAT`, which is a managed exception, so the pipeline degraded
  to audio and the window stayed up. With the mingw runtime bundled, the whole
  ANGLE path executed for the first time, and a fault in there is not an
  exception: it is an `abort()` or an access violation that ends the process
  before the window appears.

  Three changes, each addressing a different part of that:

  - `AngleContext` now reads the EGL **client extension string before calling any
    extension entry point**. ANGLE exports its extension functions
    unconditionally, so a build compiled without the Direct3D 11 backend does not
    return an error from `eglCreateDeviceANGLE` — it reaches an `UNREACHABLE()`
    and aborts. Asking first is the only way to find out safely.

  - A **second route to a shared device**. If the ANGLE build cannot adopt a
    device the app created (`EGL_ANGLE_device_creation_d3d11`), the app now
    adopts the device ANGLE created instead, via `EGL_PLATFORM_ANGLE_ANGLE` and
    `EGL_EXT_device_query`. That is ANGLE's ordinary mode of operation and works
    on every build with the D3D11 backend at all. Either way the pipeline ends up
    on one device, which is the property the zero-copy frame path rests on.

  - `RenderGuard` **brackets the attempt with a marker file**, so a native crash
    cannot become a launch loop. Finding the marker at startup means the previous
    run died inside the pipeline; video is skipped for that run and the app opens,
    plays audio, and says why. Installing a new build clears the marker.

- Faults on background threads were invisible. `Application.UnhandledException`
  only observes the UI thread, so an exception escaping the engine's event thread
  or the render thread ended the process with nothing written anywhere.
  `AppDomain.CurrentDomain.UnhandledException` now logs it first.

- The diagnostic log held the file open for the life of the process instead of
  reopening it per line. At the verbose libmpv level the startup sequence alone
  is several hundred lines, and an open-append-close for each was the slowest
  thing in the launch path. `AutoFlush` keeps the crash-survivability that the
  per-line write was there for, since the flush is to the operating system rather
  than into a buffer that dies with the process.

- Four defects surfaced by the first Windows builds, none of which any amount of
  cross-compilation on the authoring machine could have caught:
  - `PlayerViewModel`'s generated `Timecode` property shadowed the
    `Nocturne.Core.Text.Timecode` helper class inside its own type, so
    `Timecode.FormatPair` resolved against a `string`. Renamed to `TimecodeText`.
  - `TextBlock.FontFeatures` does not exist in WinUI — it is a WPF facility. The
    `WMC9999 Object reference not set` that followed was downstream of it, not a
    separate fault. Fixed-width digits already come from the monospace family.
  - `[ObservableProperty]` on a field raises `MVVMTK0045` in WinUI 3: the
    generated code is not AOT-compatible for WinRT marshalling. The documented
    fix is a partial property, needing C# 13 and a .NET 9+ SDK. Raising the SDK
    to save boilerplate on one thirteen-property view model — and keeping a
    source generator that cannot be exercised on the authoring machine — was the
    worse trade, so CommunityToolkit.Mvvm was dropped for a twenty-line
    `ObservableBase`.
  - `FirstOrDefault` on an `IReadOnlyList` (CA1826), and `MainWindow` owning two
    native resources without being disposable (CA1001).

- Thirteen defects found by two independent review passes over code that had
  never run. Four would each have stopped the first Windows session dead:

  - `SetDllImportResolver` is scoped to a single assembly. The render API's
    P/Invokes live in `Nocturne.Render` while the resolver was registered for
    `Nocturne.Engine`, so every render call would have probed for a bare
    `mpv.dll` — a file no libmpv distribution contains.
  - ANGLE was handed the app's Direct3D device through a display attribute.
    `EGL_D3D11_DEVICE_ANGLE` is a device-creation token, not a display
    attribute; at best ANGLE ignored it and silently created a second device,
    breaking the single-device invariant the whole pipeline rests on. The
    supported route is `eglCreateDeviceANGLE` followed by
    `eglGetPlatformDisplayEXT` with `EGL_PLATFORM_DEVICE_EXT`. The same call
    takes a 32-bit `EGLint` attribute list, not a pointer-sized one; the comment
    asserting otherwise was wrong.
  - `RangeBase.ValueChanged` fires for programmatic writes as well as gestures,
    and `FocusState` says nothing about where a change came from. One click on
    the seek bar would have turned every position update the engine publishes
    into a fresh seek.
  - `Slider` marks `PointerPressed` and `PointerReleased` as handled to capture
    the pointer, so the scrub-suppression handlers attached in XAML never ran at
    all. The mechanism looked wired up and was inert.

  Also: `mpv_render_context_free` called without a current GL context; native
  resources torn down under a render thread that had failed to stop, which is
  what the comment there promised not to do; `mpv_wakeup` issued after the
  writer lock was requested and so never in time, stalling every close for up to
  a second; `playlist_insert_id` declared `int` where `client.h` has `int64_t`,
  shifting every field after it; the `mpv-1.dll` fallback, whose
  `mpv_opengl_init_params` has a third field this code does not pass; the render
  failure message overwritten by ordinary snapshots within milliseconds, which
  on any machine without ANGLE reads as "the app opened and did nothing";
  `KeyDown` shortcuts dead at startup because nothing focusable exists yet;
  `Ctrl+O` advertised on the empty state and never implemented; the playlist
  mutated from the engine's pump thread; `async void OnDrop` with no `catch`, so
  one virtual drag source kills the process; a 40px title bar left across the top
  in full screen.

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
- 70 tests pass.

In CI on `windows-latest`, run `32820878154`:

- The full solution builds with **0 warnings and 0 errors**, including the XAML
  compiler pass over `MainWindow.xaml`, `Palette.xaml`, and `Controls.xaml`.
- `dotnet publish` produces a self-contained x64 app, `libmpv-2.dll` is staged
  beside it, and Inno Setup packages a 91 MB installer. Both are attached to the
  rolling `build-latest` prerelease.

### Not verified

Stated explicitly so no later reader assumes otherwise:

- **The app has never been launched.** It compiles and packages; nobody has run
  the executable. Everything that only fails at runtime — resource resolution,
  `x:Bind` against a `Window` root, whether bindings assigned after
  `InitializeComponent` update at all — is still ahead.
- **No libmpv call has ever been made by this code.** Every P/Invoke in `Engine`
  and `Render` is unexecuted.
- **No frame has been drawn.** The render pipeline has never run.
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
