# ADR 0003: Keep Core and Engine platform-neutral

- Status: Accepted
- Date: 2026-08-25

## Context

Nocturne is a Windows-only product. The obvious layering is to target
`net8.0-windows` throughout and be done with it.

The problem is verification. Most of this codebase was written on a machine that
cannot build WinUI, and even on Windows the interesting failures — a snapshot
that mixes two files, a seek that lands past the duration, a shuffle that loses
the user's ordering — are state-machine bugs that a running app surfaces slowly
and a test surfaces instantly.

A second, quieter problem: it is easy to reach for a Windows type for
convenience, and each time it happens a piece of logic moves out of reach of the
test suite. Nothing announces it.

## Decision

`Nocturne.Core` and `Nocturne.Engine` target `net8.0`, not `net8.0-windows`.
Neither may name a Windows type. Only `Nocturne.Render` and `Nocturne.App` are
Windows-bound.

The libmpv P/Invoke surface stays in the neutral `Engine` project, resolved
through a `DllImportResolver` that maps a logical `mpv` name to per-platform file
names. libmpv exists on every desktop OS, so nothing in those declarations needs
a Windows type.

`Nocturne.Render` sets `EnableWindowsTargeting` so it compiles — though it cannot
run — on the authoring machine.

## Consequences

Positive:

- The playback state machine is unit-tested in milliseconds on any machine. The
  67 tests in this repository run on macOS, on Linux in CI, and on Windows.
- The constraint is enforced by the compiler, not by review. A
  `using Microsoft.UI.Xaml` in `Engine` fails the build.
- A Linux CI job runs the tests, which means the neutrality itself is checked on
  every push rather than assumed.
- `PlaybackSnapshotReducer` exists as a set of pure functions specifically
  because of this rule, and that turned out to be the right shape regardless —
  it is the clearest code in the project.
- Interop signatures, nullability, and analyzer conformance in `Render` are
  checked on every edit rather than only in CI.

Negative:

- One more project boundary, and occasional friction placing a type.
- `net8.0` rather than the newest runtime, so `System.Threading.Lock` and other
  net9+ conveniences are unavailable. `PlayerEngine` uses a plain `object`
  monitor for this reason.
- `EnableWindowsTargeting` compiling `Render` off Windows is a genuine trap: it
  produces a green build that proves nothing about behaviour. `README.md`,
  `CHANGELOG.md`, and `WINDOWS_HANDOFF.md` all state this explicitly, because the
  green build is exactly the kind of evidence that gets over-read later.

## Not a portability claim

This is not a step toward a macOS or Linux build. `Core` and `Engine` are
portable because portability is the cheapest available proof that they contain
no UI concerns — not because a port is planned. `PLAN.md` lists cross-platform
under non-goals.
