# Changelog

All notable changes to Nocturne are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The
project will follow [Semantic Versioning](https://semver.org/) from the first
public preview; during pre-alpha, breaking changes appear under `Unreleased`.

Entries state what was verified and what was not. "Compiles" is not "works", and
this file is where that distinction is kept honest.

## [Unreleased]

### Added

- **Scrub preview.** Hovering or dragging the seek bar shows a thumbnail of the
  frame at that point, with the timecode under it.

  The frames come from a **second libmpv instance running the software
  renderer**, and both halves of that are deliberate. A second instance because
  the first one is playing, and seeking the playing instance to fetch a preview
  is the one thing this feature must never do. The software renderer because the
  GPU path is a single device, swap chain and render context that took a long
  time to make work — threading a second consumer through it to produce
  256-pixel-wide images would risk the part of the project that matters most for
  the part that matters least. A keyframe decode into a small CPU buffer costs a
  few milliseconds and shares nothing.

  Requests are coalesced, not queued: a drag produces pointer events far faster
  than frames can be decoded, and each one supersedes the last. Seeks are
  keyframe-only, which is why every player's scrub preview feels instant.

  The timecode updates from the pointer while the picture updates from the
  decoder, so they disagree for as long as a decode takes. That is the right way
  round — the label answers "where am I about to seek", which is known at once.

- Double-clicking the picture enters and leaves full screen, alongside `F11` and
  the transport-bar button. The gesture is carried by a transparent sheet over
  the video only, so a double-click on the transport bar does nothing — rather
  than the handler having to guess from `OriginalSource` which controls should be
  exempt.

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

### Changed

- **Correction: `target-colorspace-hint` was never read, so turning it off fixed
  nothing.** It was blamed for HDR files rendering as a white rectangle, and the
  reasoning — that the option promises the presenter will honour the signalled
  colour space, so libmpv stops tone-mapping — describes a real mechanism that is
  not on this render path. The option is read only by `vo_gpu_next`; mpv's manual
  marks it gpu-next only. **The white rectangle is unexplained and still open.**

- **The renderer is `vo_gpu`, not `gpu-next`.** Several comments, the README, the
  plan and ADR 0001 said this project renders through libplacebo. It does not, and
  it cannot from where it stands: `vo_libmpv` registers two backends, `gpu` and
  `sw`, and the OpenGL render API reaches `gl_video` — the older renderer. There
  is no render API type that selects libplacebo. Every quality option set here is
  a real `vo_gpu` option and does take effect; what was wrong was the reason
  written beside them. ADR 0001 carries a correction rather than a rewrite,
  because the decision it records still stands.

- `gpu-api=opengl` removed. It configures the context mpv creates for itself, and
  this app supplies the context, so nothing read it.

- `tone-mapping=bt.2390` set explicitly. `auto` resolves to it on this renderer,
  so nothing changes today — but the default differs between renderers, and the
  day this moves off `vo_gpu` is not the day to find that out.

### Fixed

- **The scrub preview stopped working permanently after one refused seek**, and
  the way to trigger it was the most ordinary gesture there is: hovering the seek
  bar right after opening a file. `loadfile` is asynchronous, a seek issued
  before it completes is answered with an error rather than ignored, and that
  error escaped the worker thread — which then exited for good, leaving the
  window holding a dead decoder and showing an empty card. A file that cannot be
  seeked at all did the same on the first request. One refused seek is now
  reported and skipped, and requests are held back until the preview instance has
  a file loaded.

- A failure part-way through starting the preview decoder leaked the libmpv
  instance behind it — with its own event thread — and the native buffer, once
  per file opened.

- The preview waited for "a frame is available" rather than for the seek. The
  render context's frame flag is set by the frame libmpv already had and stays
  set until something renders it, so a preview could show the first frame of the
  file labelled with the position under the pointer. It now waits for the seek to
  complete.

- Frames are dropped if they belong to a decoder that has since been replaced, or
  to a position the pointer has genuinely left. `ThumbnailFrame` carried its
  position for exactly this and nothing was reading it.

- **A file opened by double-clicking it played with no picture.** This is the
  white rectangle, and it was never about colour or about HDR — two rounds were
  spent there.

  A file association opens the file the moment the window is created, while the
  render pipeline is still waiting for the swap chain panel's first layout pass
  to learn its size. libmpv reaches video-output initialisation seventy-one
  milliseconds too early, finds no render context, and neither waits nor retries:

  ```
  vo/libmpv: No render context set.
  cplayer:   Error opening/initializing the selected video_out (--vo) device.
  lavf:      deselect track 0
  cplayer:   Video: no video
  ```

  It then plays the file as audio, and the panel shows a swap chain nothing has
  ever drawn into — undefined contents, in practice white.

  What made it look like a property of the file is that the *same* file played
  correctly when double-clicked again later: by then a Nocturne was already
  running, the activation was redirected into it, and its pipeline had been ready
  for minutes. The file that came out white was whichever one happened to be
  opened first.

  A launch file is now held until the render pipeline has been attempted, and
  opened on every outcome: a pipeline that could not be built is a reason to play
  the file without video, not a reason to sit on an empty window holding a path
  it never opened.

- **The per-file video description reported nothing.** It was hooked to
  `FileLoaded`, which is the obvious event and the wrong one: `video-params` is
  populated by the video output, and the video output is configured afterwards.
  The one line added to explain a rendering bug came back as a row of question
  marks on the first run it was asked about. It now runs on
  `MPV_EVENT_VIDEO_RECONFIG`, and reports the output parameters beside the source
  ones — a picture wrong in a way the source cannot explain shows up as a
  difference between the two.

Findings from four independent reviews of the render, engine, and app layers,
plus a review that designed the test suite rather than looking for defects.
Everything below was verified by reading the code; where a test could express it,
the test was written first and failed first.

- **Playback never advanced to the next file.** `keep-open=yes` stops libmpv
  unloading a file at the end — which is what holds the last frame on screen —
  and `MPV_EVENT_END_FILE` is documented to arrive only *after* an unload. So the
  event never came, `ReachedEnd` never fired, and the whole end-of-file path was
  unreachable code. The `eof-reached` property is now observed, and every route
  into `Ended` reports through one place so the event fires exactly once however
  it was reached.

- **A latent crash in the import resolver.** `RegisterCallingAssembly` added an
  assembly to a list, registered a resolver for it, and only then materialised
  the `Lazy` that walks that same list — registering it a second time.
  `SetDllImportResolver` throws on a second registration and `Lazy` caches the
  exception forever, so every later libmpv call would have failed rather than the
  one that tripped it. It never fired only because the window happens to build
  the engine before the renderer. Two independent reviews found this.

- **Disposing the client twice threw.** The second call reached
  `EnterWriteLock` on a lock the first call had disposed, before the `_disposed`
  guard inside it. Reaching that guard requires the lock to still exist.

- Non-finite volumes and speeds are refused instead of stored.
  `Math.Clamp(NaN, 0, 100)` is `NaN`, which blanks the volume slider; `±∞` clamp
  to the ends of the range and move the volume silently.

- Reaching the end of a file survives the pause that immediately follows it.
  With `keep-open-pause=yes` that pause always arrives, and treating it as an
  ordinary pause turned "finished" into "paused in the middle".

- Negative positions are clamped on the way in. libmpv echoes a pre-clamp
  `time-pos` during a seek, and the transport bar could flash `-00:01`.

- **Dragging the seek bar put the position back.** A `Slider` releases pointer
  capture as part of an ordinary click, so `PointerCaptureLost` fired on every
  seek and was being treated as an abandoned gesture. Capture loss now commits;
  only a real cancellation cancels.

- **Clicking the volume track did nothing.** The Slider's class handler moves the
  thumb and raises `ValueChanged` before the instance handler runs, so the first
  change arrived while the gesture flag was still false and was discarded — the
  thumb moved and sprang back. The click now commits.

- Leaving full screen restores a maximized window.
  `SetPresenter(Overlapped)` returns a default presenter, not the previous one.

- The swap chain follows display-scale changes. Moving the window to a display at
  a different scale usually leaves the panel the same size in DIPs, so
  `SizeChanged` never fired while the pixel count behind it changed.

- `ResizeBuffers` and `Present` results are checked. Vortice returns a `Result`
  rather than throwing, so a device removed by a driver reset was ignored: the
  loop went on reporting swaps to libmpv that never reached a screen, and the
  picture froze with nothing logged.

- Resizing builds the new surface before releasing the old one, so a failure
  halfway through leaves a working renderer instead of one holding no surface at
  all — which would have made disposal call `mpv_render_context_free` with no
  current context.

- The crash guard is closed by a frame reaching the screen, not by the
  constructor returning. The heaviest native work happens on the render thread
  after construction, so the previous timing declared success before the part
  most likely to fault had run.

- Errors raised by the window itself — an unreadable dropped item, a picker that
  would not open — survive longer than one frame. They were being written
  straight to the bound property and overwritten by the next snapshot, several
  times a second.

- The transport bar stays when the video pipeline fails. The message says audio
  still works, and hiding every control underneath that sentence left the
  keyboard as the only way to act on it.

- Pruning old logs no longer disables logging. It caught only `IOException`, so a
  read-only log file threw past it and failed the whole of `Start` — running the
  session with no log at all, in the session most likely to need one.

- A log subscriber that throws no longer kills the process. Both places that
  report through `LogMessage` are already handling a failure, and the event pump
  runs on a background thread.

- Seeking with nothing loaded is ignored rather than thrown. libmpv answers
  `seek` with `MPV_ERROR_COMMAND` when idle, which reached the UI thread as an
  exception from a keypress.

- The end-of-file handler checks for disposal before opening the next file. A
  file ending as the window closes is not a rare coincidence; it is when files
  end.

- Log file names carry the process id. A file association starts a process per
  double-click, and two starting in the same second shared one file with two
  writers.

- `core-idle` is no longer observed. It was subscribed with no case to receive
  it, so every change was marshalled across the event thread and dropped — and
  the coverage test that should have caught it had been given an exception list
  containing exactly that property. The exception list is gone.

- **HDR files rendered as a white rectangle.** Two files in the same container
  behaved differently, which looked like a decoder problem and was a colour one.

  `target-colorspace-hint=yes` shipped while the pipeline had no HDR output path.
  That option is a promise, not a request: it tells libmpv that whoever presents
  the frame will honour the signalled colour space, so libmpv stops tone-mapping
  and hands over PQ-encoded values as they are. The swap chain is
  `B8G8R8A8_UNorm` in the default sRGB colour space, and PQ values presented as
  sRGB are enormously too bright — hence white, not merely wrong. SDR files were
  unaffected, which is why it survived every earlier test.

  The hint is off. **This explanation was wrong — see the correction under
  Changed.**

- The log records what was actually decoded, once per file: codec, resolution,
  pixel format, primaries, transfer function, matrix, signal peak and the active
  hardware decoder. Colour first, because that is the field that distinguishes
  two files a container makes look identical.

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
