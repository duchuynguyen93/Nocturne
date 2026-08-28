# The frame path

This is the document to read before changing anything in `Nocturne.Render`.
Everything else in the app is replaceable; this path is the product.

## 1. What has to be true

A frame leaves the file and reaches the panel without ever being touched by the
CPU, and the app's interface composes over it with real transparency. Those two
requirements pull in opposite directions, and most of the design exists to hold
both at once.

- **No readback.** A decoded 4K frame is about 12 MB. At 24 fps that is 290 MB/s
  across the bus in each direction if the frame is copied to system memory and
  back. That cost shows up as steady CPU use, a warm machine, and a fan — the
  difference between a player that is pleasant on a laptop and one that is not.
- **Real compositing.** The transport bar is translucent, has rounded corners,
  and fades. That requires the video to be a composition visual the window
  manager can blend, not an opaque child window sitting on top of everything.
- **Correct timing.** 23.976 fps content on a 60 Hz panel needs frames resampled
  to the display's real cadence, which needs the renderer to know when frames
  actually reached the display.

## 2. The path

```
   file
     │  FFmpeg demux + D3D11VA hardware decode          (inside libmpv)
     ▼
   NV12 texture on the GPU
     │  libplacebo: scale, deband, dither, tone map     (inside libmpv)
     ▼
   GL framebuffer  ──── ANGLE translates GL → D3D11 ────┐
                                                        ▼
                                        ID3D11Texture2D owned by the app
                                                        │  CopyResource
                                                        ▼
                                        IDXGISwapChain1 back buffer
                                                        │  Present(1)
                                                        ▼
                            DirectComposition visual ── SwapChainPanel
                                                        │
                                        XAML composes the transport bar over it
```

The join at the bottom is the one that makes the interface possible. The swap
chain is created with `CreateSwapChainForComposition` rather than
`CreateSwapChainForHwnd`, which produces a swap chain with no window of its own.
`ISwapChainPanelNative::SetSwapChain` hands it to a `SwapChainPanel`, and from
that point the video is a visual in the same composition tree as the XAML — so
alpha, transforms, and animations all work across the boundary.

The join at the top is the one that makes it fast. The app's own `ID3D11Device`
is wrapped as an `EGLDeviceEXT` with `eglCreateDeviceANGLE`, and the display is
built on that device with `eglGetPlatformDisplayEXT(EGL_PLATFORM_DEVICE_EXT, …)`.
The render target is a texture the app created, wrapped as an EGL pbuffer through
`EGL_ANGLE_d3d_texture_client_buffer`. One device means no shared handles, no
keyed mutexes, and no fence wait inside the presentation interval.

There is no display *attribute* that takes a raw `ID3D11Device*`. An earlier
version of this code put `EGL_D3D11_DEVICE_ANGLE` — a device-creation token — in
the attribute list, where at best ANGLE ignores it and quietly creates a second
device of its own, which dissolves the single-device property this whole section
is about.

## 3. Why not the simpler options

**`--wid` into a child HWND.** The shortest path, and the one most embeddings
use. libmpv creates its own D3D11 swap chain on the window and presents it,
which is marginally faster than this design because there is no copy into a
composition swap chain. It also makes the interface impossible: a child window
occludes everything drawn over it, so there is no translucent transport bar. On
Windows this can be worked around by composing the two as DirectComposition
visuals, but the app cannot reach inside libmpv to get the swap chain it created,
so the workaround does not apply.

**`MPV_RENDER_API_TYPE_SW`.** libmpv renders into a system-memory buffer the app
uploads. Trivially correct and portable, and it violates requirement one on
every frame. Useful as a temporary correctness check if the ANGLE path will not
initialize; not shippable.

**Media Foundation instead of libmpv.** `MediaPlayerElement` composes correctly
with XAML with no work at all, and its renderer is not in the same category:
no libplacebo, no configurable scaling kernels, no debanding, no shader support,
and codec coverage limited to what the OS ships.

## 4. Risks

### Risk 1 — sourcing ANGLE (resolved 2026-08-26)

mpv removed its own ANGLE backend in 0.37, so libmpv Windows builds ship no
`libEGL.dll`. That does not break this design — Nocturne never used mpv's ANGLE
backend; it creates its own EGL context and passes libmpv the resulting GL entry
points through `MPV_RENDER_PARAM_OPENGL_INIT_PARAMS`, and the render API does not
care where the context came from. But the binaries had to come from somewhere.

**Resolved: the MSYS2 `mingw-w64-x86_64-angleproject` package.** An 11 MB
download from `repo.msys2.org`, pinned to an exact version in
`scripts/fetch-mpv.ps1`. Verified before adopting:

- `eglCreateDeviceANGLE` and `eglReleaseDeviceANGLE` are exported from its
  `libEGL.dll` — this is what lets ANGLE run on the app's own D3D11 device.
- `EGL_ANGLE_d3d_texture_client_buffer` is present, with
  `EGL_D3D_TEXTURE_ANGLE` at `0x33A3`, matching the constant this code uses.
- `EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE` is `0x3208`, likewise.

The package also ships a Vulkan-backed `libEGL_vulkan_secondaries.dll` and a
capture-enabled `libGLESv2_with_capture.dll`; the fetch script filters both out.

Two sources were tried and rejected, recorded so nobody repeats the search:

- **Electron and Chrome.** Modern builds link ANGLE statically into the main
  binary and ship no `libEGL.dll` at all. Checked against Electron v44, which
  contains only `d3dcompiler_47.dll`. Older Electron did ship the pair, but
  pinning a three-year-old Chromium for a graphics translator is worse than
  taking a maintained package.
- **Qt 5's bundled ANGLE.** Far too old to carry the extensions above.

The pin matters. ANGLE is the one dependency whose exact build decides whether
the pipeline works, so an unannounced upgrade must not arrive with a routine CI
run.

**Still unverified:** that this ANGLE build actually renders a frame on real
hardware. Exports and headers say the entry points exist; nothing yet says the
pipeline draws. The spike in [`WINDOWS_HANDOFF.md`](WINDOWS_HANDOFF.md) is still
the next step.

**If it fails anyway,** the fallback is unchanged: abandon the OpenGL render API,
embed via `--wid`, compose the two visuals with DirectComposition, and give up
translucent XAML over the video. That is an architectural retreat and belongs in
a superseding ADR, not a quiet patch.

### Risk 2 — orientation

GL's framebuffer origin is bottom-left; Direct3D's is top-left. `RenderFrame`
passes `MPV_RENDER_PARAM_FLIP_Y` with a value of 1. If the first frame that ever
renders is upside down, this is the flag, and the fix is one character. It is
called out here because an inverted picture looks like a catastrophic pipeline
failure and is not one.

### Risk 3 — HDR is not wired up

The swap chain is created as `B8G8R8A8_UNorm` — 8 bits per channel, SDR. HDR
passthrough needs `R10G10B10A2_UNorm`, `IDXGISwapChain4::SetColorSpace1` with
`DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020`, and `SetHDRMetaData` carrying the
source's mastering display data, driven by libmpv's `target-colorspace-hint`
reporting. `MpvOpenGlFbo.InternalFormat` must change to `GL_RGB10_A2` at the
same time, or libmpv dithers to 8 bits and discards the precision the rest of
the change exists to preserve.

None of that is implemented. `EngineOptions.TargetColorspaceHint` is on, which
means libmpv will report what it wants; nothing consumes the report yet.

### Risk 4 — no independent flip

A composition swap chain is composed by the desktop window manager. A fullscreen
video on a dedicated swap chain can instead take an independent flip path, where
the display's own overlay plane does the scaling — lower latency, less power,
and better scaling quality. Nocturne gives that up in exchange for the compositing
that the interface needs.

The eventual answer is probably to keep this path windowed and switch to a
dedicated fullscreen swap chain when the window enters fullscreen. That is a
Milestone 5 item, not a Milestone 1 one, and it should be measured before it is
built: on a modern GPU the copy this design adds is a fraction of a millisecond.

## 5. Threading

| Thread | Owns |
| --- | --- |
| UI | XAML, the view model, `Resize` requests |
| mpv event pump | property changes, end-of-file, log lines |
| render | the EGL context, `mpv_render_context_render`, `Present` |

Three rules follow, and breaking any of them produces a hang rather than an
error:

1. **The EGL context is current on the render thread and nowhere else.** It is
   made current once when the loop starts and cleared when it exits.
2. **The mpv update callback may only set an event.** It is invoked from inside
   libmpv's own locks. Calling back into libmpv from it deadlocks; so does taking
   any lock the render thread might already hold.
3. **Resizing happens on the render thread.** The UI thread records the new size
   and returns. Releasing swap chain buffers underneath a render in progress is
   a device-removed error, not a resize.

`MpvClient` uses a reader/writer lock over the handle's lifetime so that
disposal waits for in-flight calls rather than freeing the handle under them.

## 5a. Who creates the Direct3D device

There is one device. There are two ways to arrive at it, and which one runs is
decided at startup by what the ANGLE build reports it can do.

**Adoption (preferred).** The app calls `D3D11CreateDevice`, wraps the result
with `eglCreateDeviceANGLE`, and builds the display on it through
`EGL_PLATFORM_DEVICE_EXT`. The app chooses the feature level and the creation
flags, which is why it is preferred. It requires
`EGL_ANGLE_device_creation_d3d11` and `EGL_EXT_platform_device`.

**Borrowing (fallback).** The app builds an ordinary ANGLE display with
`EGL_PLATFORM_ANGLE_ANGLE` / `EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE`, then reads
the device back out with `eglQueryDisplayAttribEXT(EGL_DEVICE_EXT)` followed by
`eglQueryDeviceAttribEXT(EGL_D3D11_DEVICE_ANGLE)`, adds a reference, and uses
that. This is how every ordinary ANGLE consumer works, so it is available on any
build with the D3D11 backend at all.

Both end on one device, which is the property §2 depends on. What must never
happen is *two* devices — every frame would then need a shared handle and a keyed
mutex, and that wait lands inside the presentation interval.

### The extension string is checked first, and this is not optional

ANGLE exports its extension entry points unconditionally. An entry point whose
backend was not compiled into that build does not return an error: it reaches an
`UNREACHABLE()` and calls `abort()`. That is not an exception. No `catch` runs,
no dialog appears, and the process is gone before its window is drawn — which is
exactly what the first build to successfully load `libEGL.dll` did.

So `AngleContext.Create` reads `eglQueryString(EGL_NO_DISPLAY, EGL_EXTENSIONS)`
before touching anything else, and only calls what that string advertises.

`RenderGuard` is the belt to that braces: the attempt is bracketed by a marker
file in `%LOCALAPPDATA%\Nocturne\`, written before the first native call and
deleted after. Finding it at the next startup means the previous run did not
survive, so video is skipped and the app opens without it. Installing a build
clears the marker, because a new build deserves a fresh attempt.

## 6. The invariant that is easy to lose

`mpv_render_context_report_swap` must be called after every `Present`. libmpv
derives its frame scheduling from the interval between those reports; without
them, `video-sync=display-resample` has nothing to synchronise against and falls
back to guessing, which is visible as judder on 23.976 fps content. It is one
line, it has no return value, and nothing fails loudly if it is deleted.
