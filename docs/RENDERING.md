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

The join at the top is the one that makes it fast. ANGLE is initialized on the
app's own `ID3D11Device` via `EGL_PLATFORM_ANGLE_D3D11_DEVICE_ANGLE`, and the
render target is a texture the app created, wrapped as an EGL pbuffer through
`EGL_ANGLE_d3d_texture_client_buffer`. One device means no shared handles, no
keyed mutexes, and no fence wait inside the presentation interval.

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

### Risk 1 — ANGLE is not distributed with libmpv

**This is the largest unknown in the project.** mpv removed its own ANGLE
backend in 0.37, so current libmpv Windows builds ship no `libEGL.dll`.

That removal does not by itself break this design. Nocturne does not use mpv's
ANGLE backend; it creates its own EGL context and passes libmpv the resulting GL
entry points through `MPV_RENDER_PARAM_OPENGL_INIT_PARAMS`. libmpv's render API
does not care where the GL context came from. But the ANGLE binaries have to
come from somewhere, and that somewhere is now separate from mpv.

Sources, best first:

1. **Build ANGLE from source.** Authoritative, reproducible, pinnable. Costs a
   Chromium-style `depot_tools` checkout.
2. **Take the pair from an Electron or Chromium distribution.** Both ship
   `libEGL.dll` and `libGLESv2.dll` built from ANGLE. Fine for development;
   check redistribution terms before shipping.
3. **A Qt 5 installation.** Older Qt bundled ANGLE. Likely too old to carry
   `EGL_ANGLE_d3d_texture_client_buffer` in a usable state.

If it turns out no obtainable ANGLE build exposes the extensions this code
needs, the fallback is to abandon the OpenGL render API and embed via `--wid`,
composing the two visuals with DirectComposition and giving up the ability to
put translucent XAML over the video. That is a real architectural retreat and
should be a recorded decision, not a quiet patch.

**Verify this before writing any more render code.** The spike in
[`WINDOWS_HANDOFF.md`](WINDOWS_HANDOFF.md) exists for exactly this.

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

## 6. The invariant that is easy to lose

`mpv_render_context_report_swap` must be called after every `Present`. libmpv
derives its frame scheduling from the interval between those reports; without
them, `video-sync=display-resample` has nothing to synchronise against and falls
back to guessing, which is visible as judder on 23.976 fps content. It is one
line, it has no return value, and nothing fails loudly if it is deleted.
