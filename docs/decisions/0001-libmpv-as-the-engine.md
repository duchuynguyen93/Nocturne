# ADR 0001: Use libmpv as the playback engine

- Status: Accepted
- Date: 2026-08-25

## Context

Nocturne's stated priority is picture quality above all else. Three engines were
considered for a Windows-only player.

**A hand-written FFmpeg pipeline.** Full control: FFmpeg for demux and decode,
D3D11VA for hardware decoding, custom HLSL for colour conversion and scaling.

**Media Foundation**, through `MediaPlayerElement` or a custom topology. Native,
composes with XAML for free, hardware decode and HDR handled by the OS.

**libmpv**, the library form of mpv: FFmpeg for decode, libplacebo for render,
libass for subtitles, with a client API for control and a render API for
presentation.

The decisive observation is that decoding is not where quality differences come
from. Every option calls the same hardware decoder and gets the same frames. The
difference is in the renderer — the scaling kernel, debanding, dithering, HDR
tone mapping, and frame timing — and that is a body of work measured in
person-decades, not person-months.

## Decision

Use libmpv, driven through its client API for control and its render API for
presentation, with `vo=libmpv`. (See the correction at the end: that render
path is served by `vo_gpu`, not by `gpu-next`.)

## Consequences

Positive:

- mpv's GPU renderer, immediately: `ewa_lanczossharp` scaling, debanding,
  dithering, and HDR tone mapping that would take years to approach.
- `video-sync=display-resample`, which is what removes judder from 23.976 fps
  content on a 60 Hz panel.
- libass for subtitles — correct ASS typesetting and karaoke, which is a large
  project on its own.
- Format coverage equal to FFmpeg's, without maintaining a codec matrix.
- Custom GLSL shader support comes free, which is the honest version of the
  "AI upscaling" feature the reference product advertises.

Negative:

- **Licensing.** libmpv and FFmpeg are GPL as commonly built. This forecloses a
  closed-source release unless an LGPL build pipeline is maintained. Recorded in
  `docs/LICENSING.md`; it is the largest non-technical consequence of this ADR.
- The render API speaks OpenGL, which on Windows means ANGLE, which is its own
  dependency and its own risk. See ADR 0002.
- A large native dependency that must be shipped, versioned, and kept current.
- Debugging crosses a P/Invoke boundary into C.
- Some behaviour is configured through option strings rather than a typed API,
  and several options are silently ignored if set after initialization.

## Alternatives, and why not

**Hand-written FFmpeg + D3D11.** Rejected on the quality argument above: it
maximises control over the part that does not differentiate and requires
rebuilding the part that does. Two years of work to arrive somewhere worse.

**Media Foundation.** Rejected on renderer quality. It composes with XAML for
free — genuinely simpler than the design in ADR 0002 — but has no
configurable scaling, no debanding, no shader support, and codec coverage limited
to what Windows ships. It is the right choice for a player embedded in another
app; it is the wrong choice when the renderer is the product.

**libVLC.** Comparable format coverage and an easier embedding story, but its
renderer is not in mpv's class, and its Windows embedding path leads back
to a child window, which ADR 0002 rejects.

## Correction, 2026-08-30

This decision was recorded as choosing `gpu-next`, mpv's libplacebo renderer.
That is not what the configuration selects, and it is not selectable from where
this app stands.

`vo=libmpv` with `MPV_RENDER_API_TYPE_OPENGL` is served by `vo_gpu`, the older
renderer: `vo_libmpv.c` registers exactly two backends, `gpu` and `sw`, and the
`gpu` one calls straight into `gl_video`. There is no render API type that
selects libplacebo, and proposals to add one are still open upstream. mpv's own
manual says as much in its `libmpv` VO entry — "supports many of the options the
`gpu` VO has".

The decision to use libmpv still stands, and every quality option this project
sets is a real `vo_gpu` option that takes effect. What was wrong was the reason
written down, and a reason that cannot be checked is how a project ends up
defending a property it does not have.

