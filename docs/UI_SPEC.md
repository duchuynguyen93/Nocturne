# Design system

## 1. Where this came from

The visual language is taken from [Lumen Player](https://lumenplayer.net/). It
is worth being precise about what that means, because it bounds what "clone"
can honestly claim here.

Lumen does not publish screenshots of its application. The "product tour" on its
site is a set of stylised CSS mock-ups — gradient rectangles standing in for
artwork, an abstract sidebar, no real transport bar. There is no source image to
match pixel for pixel.

What *is* available, and what was used, is Lumen's design system itself, read
out of the computed styles on their marketing site: the full neutral ramp, the
accent pair, the type families, the radius scale, and the blur scale. Those are
reproduced here exactly. The layout of the player window is then derived from
their hero mock-up, which does show a transport bar, and from the constraints of
the medium.

So: **the language is a faithful copy; the screen layout is a reconstruction.**
If real screenshots surface later, this document is the thing to revise.

## 2. Tokens

Defined in `src/Nocturne.App/Design/Palette.xaml`.

### Neutrals — "ink"

| Token | Value | Used for |
| --- | --- | --- |
| `Ink950` | `#0A0A0B` | window background |
| `Ink900` | `#111114` | recessed surfaces |
| `Ink850` | `#16161B` | cards, panels |
| `Ink800` | `#1A1A1F` | raised surfaces |
| `Ink700` | `#26262E` | borders |
| `Ink600` | `#3D3D48` | strong borders, disabled fills |
| `Ink500` | `#6B6B7A` | disabled text |
| `Ink400` | `#828292` | muted text |
| `Ink300` | `#A0A0AD` | secondary text |
| `Ink200` | `#C7C7D1` | — |
| `Ink100` | `#E8E8ED` | — |
| `Ink50` | `#F8F8FA` | primary text |

The ramp is neutral but very slightly cool — `#0A0A0B` is not `#0A0A0A`. That is
deliberate in the source and preserved here: a truly neutral near-black next to
video reads as slightly warm by contrast.

### Accent

| Token | Value | Used for |
| --- | --- | --- |
| `Amber500` | `#F5A623` | play button fill, progress fill, focus |
| `Amber400` | `#FFB627` | hover |
| `Success` | `#34D399` | confirmations |
| `Danger` | `#F87171` | failures |

**One accent hue, used sparingly.** The play button and the progress fill are
the only amber objects on the surface. That is what lets the eye find the
transport instantly over an arbitrary frame, and it is the constraint most
likely to be broken by a later feature that "just needs one more highlight".

### Radius

`6 / 8 / 12 / 16` and a pill. Overlay chips use 8, panels use 12, dialogs 16.

### Type

| Role | Family | Fallback |
| --- | --- | --- |
| Display | Cabinet Grotesk ExtraBold | Segoe UI Variable Display |
| UI | Geist | Segoe UI Variable Text |
| Mono | Geist Mono | Cascadia Mono, Consolas |

Neither Cabinet Grotesk nor Geist ships with Windows. Each `FontFamily` names a
bundled file first and falls through to a Windows face in the same role, so the
app is fully legible with no bundled fonts present — the display face just loses
its character.

To add the real fonts, drop the files into `src/Nocturne.App/Assets/Fonts/` as
`CabinetGrotesk-Extrabold.ttf`, `Geist-Regular.ttf`, and `GeistMono-Regular.ttf`.
Geist is OFL. Cabinet Grotesk is free from Fontshare under terms worth reading
before shipping a binary — see [`LICENSING.md`](LICENSING.md).

## 3. The player surface

```
┌────────────────────────────────────────────────────────────┐
│                interstellar-journey.mkv                  ─ □ ×│  40px title bar
├────────────────────────────────────────────────────────────┤
│                                                            │
│                                                            │
│                         video                              │
│                                                            │
│  ┌──────────────────────────────┐                          │
│  │ interstellar-journey.mkv · 01:42 / 04:28 │              │  overlay chip
│  └──────────────────────────────┘                          │
│  ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁ scrim ▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁▁  │
│  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━  │  4px seek track
│                                                            │
│   ◀   ⏵   ▶   🔊 ────           01:42 / 04:28   CC   ⛶     │
└────────────────────────────────────────────────────────────┘
```

Four decisions carry the look:

**The scrim is a gradient, not a strip.** A filled bar across the bottom of the
frame is visible on every dark scene. A gradient from transparent to 85% black
over the lower third is invisible on a dark scene and still legible on a bright
one.

**The play button is the only filled shape.** 48px, amber, with a dark glyph
punched out. Everything else is a bare 36px glyph with no chrome until hovered,
and then only a faint disc — a rounded rectangle would read as a Windows toolbar
dropped onto a film.

**The seek thumb only exists on hover.** At rest the seek bar is a 4px line:
grey track, amber fill, no handle. That is a progress indicator. Under the
pointer the track grows to 6px and a 12px thumb fades in, and it becomes a
control.

**Timecodes are mono with tabular figures.** `FontFeatures="tnum"` on
`TimecodeTextStyle`. Without it, proportional digits change width as they count
and the whole right-hand cluster jitters horizontally once a second, which is
the single most distracting thing a transport bar can do. `Timecode.FormatPair`
handles the other half of the same problem by choosing one shape for both halves
of the pair.

## 4. Dark only

There is no light theme and there is not intended to be one. The chrome sits
directly beside the image it is showing; a light surround changes the perceived
black level of the video next to it. Every serious player is dark for this
reason, and offering a light theme would mean offering a worse way to watch.

High contrast is a different question — that is an accessibility requirement, not
a preference, and it is tracked in `PLAN.md` under Milestone 4.

## 5. What is not built yet

The source design also shows a library grid, a sidebar with Plex and Jellyfin
sources, a 10-band equaliser, and a plugin panel. None of that exists here. The
tokens and control styles are in place to build them against; the screens are
not designed. See `PLAN.md`.
