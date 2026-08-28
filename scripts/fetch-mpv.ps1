<#
.SYNOPSIS
    Downloads the native runtime Nocturne needs into native\win-<arch>\.

.DESCRIPTION
    Two separate dependencies land in the same folder:

      libmpv-2.dll            the player core (FFmpeg, libplacebo, libass)
      libEGL.dll              ANGLE, the OpenGL ES to Direct3D 11 translator
      libGLESv2.dll
      libgcc_s_seh-1.dll      mingw runtime that ANGLE's build links against
      libstdc++-6.dll
      libwinpthread-1.dll
      zlib1.dll               imported by libGLESv2 and not present on Windows

    libmpv is fetched automatically from the shinchiro build of mpv, which is
    the de facto official Windows distribution.

    ANGLE is not bundled with libmpv: mpv removed its own ANGLE backend in 0.37,
    so current libmpv builds ship no EGL at all. Nocturne does not use mpv's
    ANGLE backend — it creates its own ANGLE context and hands libmpv the
    resulting GL entry points through the render API — but the DLLs still have to
    come from somewhere.

    They are fetched from the MSYS2 mingw64 'angleproject' package, along with
    the three mingw runtime libraries that build links against. Two other sources
    were tried and rejected: current Electron and Chrome build ANGLE statically
    into the main binary and ship no libEGL.dll at all, and Qt 5's bundled ANGLE
    is far too old to carry the extensions this code needs.

    Nothing this script downloads is committed; native\ is git-ignored.

.PARAMETER Architecture
    x64 or arm64. Defaults to the host architecture.

.PARAMETER AnglePath
    Folder containing libEGL.dll and libGLESv2.dll, to use instead of the MSYS2
    package — for testing a different ANGLE build.

.PARAMETER Force
    Re-download even when the files are already present.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture,

    [string]$AnglePath,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Architecture) {
    $Architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
        'arm64'
    } else {
        'x64'
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot "native\win-$Architecture"
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$mpvDll = Join-Path $targetDir 'libmpv-2.dll'

function Get-LatestMpvDevAsset {
    param([string]$Arch)

    # The dev package is the one carrying libmpv-2.dll and the headers; the
    # ordinary package contains mpv.exe and no importable library.
    $releasesUrl = 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest'
    Write-Host "Querying $releasesUrl"

    $headers = @{ 'User-Agent' = 'nocturne-fetch-mpv' }
    if ($env:GITHUB_TOKEN) {
        # Unauthenticated GitHub API calls are rate limited to 60 an hour, which
        # a CI matrix exhausts quickly.
        $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
    }

    $release = Invoke-RestMethod -Uri $releasesUrl -Headers $headers

    $pattern = if ($Arch -eq 'arm64') { 'mpv-dev-aarch64-*' } else { 'mpv-dev-x86_64-*' }
    $asset = $release.assets |
        Where-Object { $_.name -like $pattern -and $_.name -like '*.7z' } |
        Sort-Object name -Descending |
        Select-Object -First 1

    if (-not $asset) {
        throw "No asset matching '$pattern' in release $($release.tag_name)."
    }

    return $asset
}

if ((Test-Path $mpvDll) -and -not $Force) {
    Write-Host "libmpv-2.dll already present. Use -Force to re-download."
} else {
    $asset = Get-LatestMpvDevAsset -Arch $Architecture
    $archive = Join-Path ([System.IO.Path]::GetTempPath()) $asset.name

    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing

    $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
    if (-not $sevenZip) {
        throw @'
7z is required to unpack the mpv archive and was not found on PATH.
Install it with:  winget install 7zip.7zip
GitHub-hosted Windows runners already have it.
'@
    }

    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) "mpv-dev-$Architecture"
    if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }

    & 7z x $archive "-o$extractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7z exited with $LASTEXITCODE." }

    $found = Get-ChildItem -Path $extractDir -Filter 'libmpv-2.dll' -Recurse |
        Select-Object -First 1
    if (-not $found) {
        throw "The archive did not contain libmpv-2.dll. Layout may have changed."
    }

    Copy-Item $found.FullName $mpvDll -Force
    Remove-Item $archive -Force
    Remove-Item $extractDir -Recurse -Force

    Write-Host "Placed $mpvDll"
}

# ── ANGLE ────────────────────────────────────────────────────────────────────

# Pinned rather than "latest": ANGLE is the one dependency whose exact build
# decides whether the render pipeline works at all, so an unannounced upgrade
# must never arrive silently with an ordinary CI run.
#
# The MSYS2 build is compiled with GCC, so these two DLLs import a set of
# libraries Windows does not have. Read out of the PE import tables, they are:
#
#   libEGL.dll      libgcc_s_seh-1, libwinpthread-1, libstdc++-6
#   libGLESv2.dll   the same three, plus zlib1
#
# None of them is optional, and getting the list wrong does not fail loudly.
#
# Omitting the first three produced a build that installed cleanly and then
# failed to load libEGL with ERROR_BAD_EXE_FORMAT (0x8007000B) — Windows had
# fallen back to whatever same-named library the machine happened to have on
# PATH. Shipping them makes the app's own directory win the search and takes
# the machine's PATH out of the equation.
#
# Omitting zlib1 was worse, because libEGL is a 260 KB forwarding shim that does
# not import libGLESv2 at all — it loads it at the first call. So libEGL loaded
# perfectly, every check passed, and the process died inside the first EGL call
# with no exception and nothing in any log. The lesson is in the CI job: every
# DLL shipped is now actually loaded there, which is the only check that sees
# this class of fault.
$anglePackages = @(
    @{ Package = 'mingw-w64-x86_64-angleproject-2.1.r25748.890b5d8f-4-any.pkg.tar.zst'
       Files   = @('libEGL.dll', 'libGLESv2.dll') }
    @{ Package = 'mingw-w64-x86_64-gcc-libs-16.2.0-3-any.pkg.tar.zst'
       Files   = @('libgcc_s_seh-1.dll', 'libstdc++-6.dll') }
    @{ Package = 'mingw-w64-x86_64-libwinpthread-git-12.0.0.r747.g1a99f8514-1-any.pkg.tar.zst'
       Files   = @('libwinpthread-1.dll') }
    @{ Package = 'mingw-w64-x86_64-zlib-1.3.2-2-any.pkg.tar.zst'
       Files   = @('zlib1.dll') }
)

$angleFiles = @($anglePackages | ForEach-Object { $_.Files } | ForEach-Object { $_ })

if ($AnglePath) {
    foreach ($name in $angleFiles) {
        $source = Join-Path $AnglePath $name
        if (-not (Test-Path $source)) {
            throw "$name not found in $AnglePath."
        }

        Copy-Item $source (Join-Path $targetDir $name) -Force
        Write-Host "Placed $(Join-Path $targetDir $name)"
    }
} elseif ($Architecture -ne 'x64') {
    Write-Warning "The MSYS2 ANGLE package is x64 only. Supply -AnglePath for $Architecture."
} else {
    # @() is required: under Set-StrictMode, Where-Object yields $null when
    # nothing matches and a bare object when exactly one does. Neither has a
    # Count property, and the line below would throw on a clean machine.
    $present = @($angleFiles | Where-Object { Test-Path (Join-Path $targetDir $_) })
    if ($present.Count -eq $angleFiles.Count -and -not $Force) {
        Write-Host "ANGLE already present. Use -Force to re-download."
    } else {
        $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
        if (-not $sevenZip) {
            throw '7z is required to unpack the ANGLE package and was not found on PATH.'
        }

        foreach ($spec in $anglePackages) {
            $package = $spec.Package
            $archive = Join-Path ([System.IO.Path]::GetTempPath()) $package
            Write-Host "Downloading $package"
            Invoke-WebRequest -Uri "https://repo.msys2.org/mingw/mingw64/$package" `
                -OutFile $archive -UseBasicParsing

            $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) 'nocturne-msys2'
            if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }

            # Two passes: .tar.zst decompresses to .tar, which then unpacks.
            # Windows' own tar cannot be relied on to read zstd.
            & 7z x $archive "-o$extractDir" -y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7z exited with $LASTEXITCODE unpacking zstd." }

            $tarFile = Get-ChildItem -Path $extractDir -Filter '*.tar' | Select-Object -First 1
            if (-not $tarFile) { throw "$package did not contain a tar archive." }

            & 7z x $tarFile.FullName "-o$extractDir" -y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "7z exited with $LASTEXITCODE unpacking tar." }

            foreach ($name in $spec.Files) {
                # Restricted to bin\: the same packages carry import libraries
                # under lib\ whose names differ only by a .a suffix, and the
                # Windows file matcher behind -Filter can match those too.
                $found = Get-ChildItem -Path $extractDir -Filter $name -Recurse |
                    Where-Object {
                        $_.Extension -eq '.dll' -and
                        $_.DirectoryName -match '\\bin$' -and
                        $_.FullName -notmatch 'vulkan_secondaries|with_capture'
                    } |
                    Select-Object -First 1
                if (-not $found) { throw "$name was not in $package." }

                Copy-Item $found.FullName (Join-Path $targetDir $name) -Force
                Write-Host "Placed $(Join-Path $targetDir $name)"
            }

            Remove-Item $archive -Force
            Remove-Item $extractDir -Recurse -Force
        }
    }
}

$missing = @(@('libmpv-2.dll') + $angleFiles |
    Where-Object { -not (Test-Path (Join-Path $targetDir $_)) })

if ($missing) {
    Write-Warning "Still missing from $targetDir : $($missing -join ', ')"
    Write-Warning "Re-run with -AnglePath <folder>. See docs/RENDERING.md."
    exit 2
}


Write-Host "Native runtime complete in $targetDir" -ForegroundColor Green
