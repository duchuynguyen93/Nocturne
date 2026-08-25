# Licensing

## Decision

**Nocturne is GPL-3.0-or-later.** See [`LICENSE`](../LICENSE).

The project is for personal use and, if it is ever published, a free
open-source release. That makes GPL the natural choice rather than a
constraint: the `libmpv-2.dll` that `scripts/fetch-mpv.ps1` downloads is a GPL
build, so distributing a binary already means distributing under GPL terms.
Choosing GPL-3.0 aligns the project's own licence with the obligation it
already carries, and removes the LGPL build pipeline described below from the
list of things that ever need doing.

The rest of this document explains what that forecloses, so a later change of
mind is made with the facts rather than against them.

## What the dependency actually requires

Shipping the GPL `libmpv-2.dll` means the whole application is distributed under
GPL terms, including Nocturne's own source. That is fine for a personal build
and fine for an open-source release. It is not fine for a closed-source or
commercial one.

## Why

mpv and FFmpeg can each be built two ways.

**FFmpeg** is LGPL 2.1+ at its core, but `--enable-gpl` pulls in GPL components —
`libx264`, `libx265`, `libpostproc`, and several filters. Any build with those
enabled is GPL, and effectively every convenience build has them, because that is
what makes the build useful for encoding.

**mpv** is LGPL 2.1+ when built with `--enable-lgpl`, and GPL otherwise. The
default is GPL.

The shinchiro builds used by the fetch script are general-purpose mpv builds:
GPL FFmpeg, GPL mpv.

## What that means in practice

| Intent | What is required |
| --- | --- |
| Personal use, not distributed | Nothing. Licence terms govern distribution. |
| **Open-source release under GPL-3.0** | **The chosen path.** Nothing beyond the usual notices. |
| Closed-source or commercial release | Build LGPL FFmpeg (no `--enable-gpl`, no x264/x265) and LGPL mpv (`--enable-lgpl`), link dynamically, and ship the LGPL notices and relink information. |

The LGPL route is real work — a custom FFmpeg and mpv build pipeline, plus the
loss of some decoders — and retrofitting it means re-testing every format. It is
not planned. If it is ever wanted, it has to happen before the codebase grows
much further, and it supersedes the decision at the top of this file.

## Component summary

| Component | Licence | Notes |
| --- | --- | --- |
| libmpv | LGPL 2.1+ or GPL 2+ | depends on build flags; default GPL |
| FFmpeg | LGPL 2.1+ or GPL 2+ | GPL once x264/x265/postproc are enabled |
| libplacebo | LGPL 2.1+ | mpv's `gpu-next` renderer |
| libass | ISC | subtitle rendering |
| ANGLE | BSD 3-clause | permissive; see `RENDERING.md` for where to obtain it |
| Windows App SDK | MIT | |
| Vortice.Windows | MIT | Direct3D/DXGI bindings |
| Geist / Geist Mono | SIL OFL 1.1 | embeddable; keep the licence file alongside |
| Cabinet Grotesk | Fontshare licence | free, but **read the terms before shipping** — it is not OFL |

## Fonts

Neither display font ships with Windows, and neither is committed to this
repository. `Design/Palette.xaml` names bundled files first and falls back to
Windows faces, so the app is fully functional without them.

If you add them, add the licence files to `src/Nocturne.App/Assets/Fonts/`
alongside, and list them in a `THIRD_PARTY_NOTICES.md` before any release.

## Nocturne's own code

GPL-3.0-or-later, in [`LICENSE`](../LICENSE).

Two consequences worth knowing before the repository is ever made public:

- Anyone who receives a binary is entitled to the corresponding source. Keeping
  the repository public once binaries are distributed is the simplest way to
  satisfy that.
- Code cannot be taken from this project into a closed-source one later, by
  anybody including its author, without relicensing — which requires every
  contributor's agreement.
