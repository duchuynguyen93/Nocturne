# ADR 0002: Present into a composition swap chain, not a child window

- Status: Accepted
- Date: 2026-08-25

## Context

libmpv can be embedded two ways.

**`--wid`.** Hand libmpv a window handle. It creates its own Direct3D 11 swap
chain on that window and presents to it. Two lines of code, the fastest possible
path, and the way most embeddings work.

**The render API.** libmpv renders into a framebuffer the app supplies, and the
app presents. More work: the app owns a graphics device, a swap chain, and a
render thread.

The interface Nocturne is built to have — a translucent transport bar on a
gradient scrim, rounded overlay chips, fades — decides between them. A child
window is not part of the compositing tree of the XAML drawn near it; it
occludes everything in its rectangle. There is no translucent anything over a
`--wid` surface.

On Windows this is not absolutely fatal: DirectComposition can compose two
visuals, one being the video swap chain and one the UI. But the app cannot reach
inside libmpv to obtain the swap chain libmpv created for the window it was
given, so the workaround does not apply to `--wid`.

## Decision

Use the render API. Create an `IDXGISwapChain1` with
`CreateSwapChainForComposition`, render libmpv's output into it, and attach it to
a `SwapChainPanel` via `ISwapChainPanelNative::SetSwapChain`.

Because the render API speaks OpenGL and everything else on Windows speaks
Direct3D, ANGLE sits between them — initialized on the app's own `ID3D11Device`
via `EGL_PLATFORM_ANGLE_D3D11_DEVICE_ANGLE`, rendering into an app-owned texture
wrapped through `EGL_ANGLE_d3d_texture_client_buffer`. One device, so no shared
handles and no keyed-mutex wait inside the presentation interval.

## Consequences

Positive:

- XAML composes over the video with real alpha. This is the entire reason.
- The app owns presentation, so frame pacing, latency, and eventually HDR
  metadata are all under its control.
- `MaximumFrameLatency = 1` instead of the default 3, removing roughly two frames
  of delay between a seek and the picture changing.

Negative:

- **ANGLE becomes a hard dependency, and mpv stopped shipping it in 0.37.** The
  binaries must be sourced separately, and whether an obtainable build exposes
  the required extensions on an external device is unverified. This is the
  project's largest open risk; see `docs/RENDERING.md` Risk 1.
- One extra full-resolution GPU copy per frame, from the render texture into the
  back buffer. Sub-millisecond on any modern GPU, but not free.
- No independent flip. A composition swap chain is composed by the desktop window
  manager, so the display's overlay plane cannot take over scaling in fullscreen.
  See `docs/RENDERING.md` Risk 4.
- Considerably more code: a device, a swap chain, an EGL context, a render
  thread, and deferred resize handling — all of it interop, none of it testable
  off Windows.

## Fallback

If ANGLE proves unobtainable in a usable form, the retreat is `--wid` plus a
DirectComposition tree, accepting that the transport bar becomes opaque and
rectangular. That would be a real change in what the product is, and it should be
recorded as a superseding ADR rather than patched in quietly.
