# Nocturne

A Windows media player built for picture quality first.

Nocturne is a WinUI 3 shell over libmpv. The engine is the same one mpv uses —
FFmpeg for decoding, libplacebo for rendering — and the app's job is to present
it well: a composition pipeline that keeps decoded frames on the GPU from
demuxer to display, and an interface that stays out of the way of the picture.

The visual language follows [Lumen Player](https://lumenplayer.net/): a
near-black neutral ramp, a single amber accent, mono timecodes, and chrome that
floats over the frame instead of framing it. See
[`docs/UI_SPEC.md`](docs/UI_SPEC.md) for the tokens and the reasoning.

> **Status: pre-alpha.** The portable layers are built and tested. The Windows
> render pipeline compiles but has never run — no frame has been drawn by this
> code on real hardware. See [Current state](#current-state) before expecting it
> to play anything.

## Why this architecture

The hard part of a video player is not the interface, it is the frame path.
Three decisions follow from that:

**libmpv as the engine, not a hand-written FFmpeg pipeline.** Decoding is
commodity — every player calls the same hardware decoder. What separates a good
picture from a mediocre one is the renderer: scaling kernels, debanding,
dithering, HDR tone mapping, and frame timing. libplacebo has a decade of work
in it. Writing a replacement would take years to reach parity and would look
worse the whole time.

**A composition swap chain, not a child window.** The obvious way to embed a
video engine is to hand it a child `HWND`. That path is fast, and it makes the
interface impossible: a child window always covers what is drawn near it, so
there is no translucent transport bar, no rounded corner, no fade. Nocturne
instead presents into a swap chain created for composition and hands it to a
`SwapChainPanel`, so XAML composes over the video with real alpha while the
video keeps its flip-model presentation path.

**ANGLE between the two.** libmpv's render API speaks OpenGL. Direct3D 11 is
what the composition engine and the hardware decoder speak. ANGLE translates,
and — given the app's own D3D11 device — does it without a copy through system
memory. This is also the pipeline's biggest external dependency and its main
risk; see [`docs/RENDERING.md`](docs/RENDERING.md).

The full picture is in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), and the
decisions are recorded as ADRs in [`docs/decisions/`](docs/decisions).

## Layout

```
src/
  Nocturne.Core      platform-neutral: playback state, timecodes, playlist, formats
  Nocturne.Engine    libmpv interop and the state machine over it (platform-neutral)
  Nocturne.Render    Direct3D 11, ANGLE, and the mpv render context (Windows only)
  Nocturne.App       WinUI 3 shell, design system, view models
tests/
  Nocturne.Core.Tests   exercises Core and Engine on any platform
```

`Core` and `Engine` are deliberately platform-neutral. That is not portability
for its own sake — it means the state machine that decides what the transport
bar shows can be unit-tested on any machine, including the macOS one most of
this was written on, instead of only inside a running Windows app.

## Current state

| Layer | Built | Verified |
| --- | --- | --- |
| `Nocturne.Core` | yes | 67 tests pass locally and in CI |
| `Nocturne.Engine` | yes | state machine tested; interop compiles, never called |
| `Nocturne.Render` | yes | compiles clean; **never executed** |
| `Nocturne.App` | yes | XAML compiles, CI publishes an installer; **never launched** |

CI produces a signed-nothing, installable build on every push to `main` — see the
[`build-latest` release](../../releases/tag/build-latest). That build does not
play anything: ANGLE is not bundled, so it launches and reports a render
initialization failure.

Nothing in the Windows-only path has drawn a frame. The first task on a Windows
machine is the spike in [`docs/WINDOWS_HANDOFF.md`](docs/WINDOWS_HANDOFF.md),
which proves the composition pipeline before any feature work continues.

## Building

On any platform, for the layers that do not need Windows:

```bash
dotnet test tests/Nocturne.Core.Tests/Nocturne.Core.Tests.csproj
dotnet build src/Nocturne.Render/Nocturne.Render.csproj -p:Platform=x64
```

On Windows, for everything:

```powershell
./scripts/fetch-mpv.ps1 -Architecture x64 -AnglePath <folder with libEGL.dll>
dotnet build Nocturne.sln -c Debug -p:Platform=x64
```

`dotnet build Nocturne.sln` does not work off Windows — `Nocturne.App` needs the
WinUI toolchain. Build the projects individually there, as above.

See [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) for prerequisites and for where
to obtain the native runtime.

## Licensing

Nocturne's own code and the libraries it links are under different terms, and
the difference decides whether the result can be distributed. libmpv and FFmpeg
can be built as LGPL or as GPL depending on which components are enabled, and
the readily available Windows builds are GPL. Read
[`docs/LICENSING.md`](docs/LICENSING.md) before publishing a binary.

## Documentation

- [`PLAN.md`](PLAN.md) — milestones and acceptance criteria
- [`CHANGELOG.md`](CHANGELOG.md) — what changed and why
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — layers and their contracts
- [`docs/RENDERING.md`](docs/RENDERING.md) — the frame path, in detail
- [`docs/UI_SPEC.md`](docs/UI_SPEC.md) — the design system
- [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) — prerequisites and workflow
- [`docs/WINDOWS_HANDOFF.md`](docs/WINDOWS_HANDOFF.md) — what to verify first
- [`docs/LICENSING.md`](docs/LICENSING.md) — third-party terms
