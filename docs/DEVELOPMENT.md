# Development

## Prerequisites

| | Version | Notes |
| --- | --- | --- |
| .NET SDK | per `global.json` | pinned at 8.0.400 with `rollForward: latestMajor`, so a newer major SDK satisfies it |
| Windows | 10 21H2 / 11 | for `Nocturne.App` and `Nocturne.Render` only |
| Visual Studio 2022 | 17.10+ | **.NET desktop** + **Windows application development** workloads |
| 7-Zip | any | on `PATH`, for `scripts/fetch-mpv.ps1` |

## Native runtime

Two dependencies, fetched into `native/win-<arch>/`, which is git-ignored.

```powershell
./scripts/fetch-mpv.ps1 -Architecture x64 -AnglePath <folder>
```

- `libmpv-2.dll` — fetched automatically from the shinchiro mpv build.
- `libEGL.dll`, `libGLESv2.dll` — **not** distributed with mpv and must be
  supplied. See [`RENDERING.md`](RENDERING.md#risk-1--angle-is-not-distributed-with-libmpv).

The script exits with code 2 when ANGLE is still missing, which CI treats as
expected. Without ANGLE the app builds and launches and reports a render
initialization failure in the window.

## Building

Off Windows — the layers that do not need it:

```bash
dotnet test tests/Nocturne.Core.Tests/Nocturne.Core.Tests.csproj
dotnet build src/Nocturne.Engine/Nocturne.Engine.csproj
dotnet build src/Nocturne.Render/Nocturne.Render.csproj -p:Platform=x64
```

`Nocturne.Render` compiles off Windows because of `EnableWindowsTargeting`. That
checks signatures and analyzers; it runs nothing.

`dotnet build Nocturne.sln` fails off Windows — `Nocturne.App` needs the WinUI
toolchain. Build the projects individually.

On Windows:

```powershell
dotnet restore Nocturne.sln -p:Platform=x64
dotnet build Nocturne.sln -c Debug -p:Platform=x64
dotnet run --project src/Nocturne.App/Nocturne.App.csproj -c Debug -p:Platform=x64
```

## Conventions

`.editorconfig` is authoritative and CI enforces it. The parts worth knowing:

- File-scoped namespaces, explicit accessibility modifiers, 4-space indent.
- `TreatWarningsAsErrors` everywhere, with `AnalysisLevel` at
  `latest-recommended`. A warning is a build failure, deliberately.
- Analyzer suppressions live in the `.csproj` with a comment explaining why,
  never inline and never repeated. The current ones:
  - `Engine`: `CA1401` (P/Invoke visibility — all entry points are private) and
    `CA1720` (`MpvFormat.String`/`Double`/`Int64` mirror `mpv_format` in
    `client.h`, and renaming them makes the mirror uncheckable).
  - `Render`: the same plus `CA1707`, for the `EGL_`/`GL_` constant spellings.
  - Tests: `CA1707`, because underscored test names are what makes a CI failure
    readable.

## Testing

```bash
dotnet test tests/Nocturne.Core.Tests/Nocturne.Core.Tests.csproj
```

The test project references both `Core` and `Engine`, which is possible because
neither targets Windows. That is the point of the layering: the playback state
machine is exercised in milliseconds on any machine.

What is testable and what is not:

| Testable here | Needs Windows |
| --- | --- |
| timecode formatting, seek clamping | any P/Invoke |
| snapshot reduction from property changes | the render pipeline |
| playlist, repeat, shuffle | XAML, bindings, resources |
| subtitle sidecar matching | frame timing, hardware decode |

When adding engine behaviour, put the decision in `PlaybackSnapshotReducer` as a
pure function and test it. `PlayerEngine` should stay thin enough that its own
correctness is obvious by inspection.

## CI

`.github/workflows/build.yml`, three jobs:

1. **portable-tests** on Linux — runs first. Also proves `Core` and `Engine` have
   not quietly acquired a Windows dependency.
2. **windows-build** — fetches libmpv, builds the solution, publishes a
   self-contained x64 app, and builds the Inno Setup installer. Both are uploaded
   as artifacts.
3. **formatting** — `dotnet format --verify-no-changes`, kept in its own job so a
   formatting nit never stands between a change and a downloadable build.

To get a build to test on Windows: open the run in the Actions tab and download
`nocturne-x64-setup` (installer) or `nocturne-x64-portable` (unzip and run).
Remember that without ANGLE beside the executable it will launch and report a
render failure — that is the expected state until Risk 1 is resolved.
