<#
.SYNOPSIS
    Downloads the native runtime Nocturne needs into native\win-<arch>\.

.DESCRIPTION
    Two separate dependencies land in the same folder:

      libmpv-2.dll            the player core (FFmpeg, libplacebo, libass)
      libEGL.dll              ANGLE, the OpenGL ES to Direct3D 11 translator
      libGLESv2.dll

    libmpv is fetched automatically from the shinchiro build of mpv, which is
    the de facto official Windows distribution.

    ANGLE is not bundled with libmpv: mpv removed its own ANGLE backend in 0.37,
    so current libmpv builds ship no EGL at all. Nocturne does not use mpv's
    ANGLE backend — it creates its own ANGLE context and hands libmpv the
    resulting GL entry points through the render API — but the DLLs still have to
    come from somewhere.

    They are fetched from the MSYS2 mingw64 'angleproject' package. Two other
    sources were tried and rejected: current Electron and Chrome build ANGLE
    statically into the main binary and ship no libEGL.dll at all, and Qt 5's
    bundled ANGLE is far too old to carry the extensions this code needs.

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
$anglePackage = 'mingw-w64-x86_64-angleproject-2.1.r25748.890b5d8f-4-any.pkg.tar.zst'
$angleFiles = @('libEGL.dll', 'libGLESv2.dll')

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
    $present = $angleFiles | Where-Object { Test-Path (Join-Path $targetDir $_) }
    if ($present.Count -eq $angleFiles.Count -and -not $Force) {
        Write-Host "ANGLE already present. Use -Force to re-download."
    } else {
        $sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
        if (-not $sevenZip) {
            throw '7z is required to unpack the ANGLE package and was not found on PATH.'
        }

        $archive = Join-Path ([System.IO.Path]::GetTempPath()) $anglePackage
        Write-Host "Downloading $anglePackage"
        Invoke-WebRequest -Uri "https://repo.msys2.org/mingw/mingw64/$anglePackage" `
            -OutFile $archive -UseBasicParsing

        $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) 'nocturne-angle'
        if (Test-Path $extractDir) { Remove-Item $extractDir -Recurse -Force }

        # Two passes: .tar.zst decompresses to .tar, which then unpacks. Windows'
        # own tar cannot be relied on to read zstd.
        & 7z x $archive "-o$extractDir" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z exited with $LASTEXITCODE unpacking zstd." }

        $tarFile = Get-ChildItem -Path $extractDir -Filter '*.tar' | Select-Object -First 1
        if (-not $tarFile) { throw 'The ANGLE package did not contain a tar archive.' }

        & 7z x $tarFile.FullName "-o$extractDir" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7z exited with $LASTEXITCODE unpacking tar." }

        foreach ($name in $angleFiles) {
            $found = Get-ChildItem -Path $extractDir -Filter $name -Recurse |
                Where-Object { $_.FullName -notmatch 'vulkan_secondaries|with_capture' } |
                Select-Object -First 1
            if (-not $found) { throw "$name was not in the ANGLE package." }

            Copy-Item $found.FullName (Join-Path $targetDir $name) -Force
            Write-Host "Placed $(Join-Path $targetDir $name)"
        }

        Remove-Item $archive -Force
        Remove-Item $extractDir -Recurse -Force
    }
}

$missing = @('libmpv-2.dll', 'libEGL.dll', 'libGLESv2.dll') |
    Where-Object { -not (Test-Path (Join-Path $targetDir $_)) }

if ($missing) {
    Write-Warning "Still missing from $targetDir : $($missing -join ', ')"
    Write-Warning "Re-run with -AnglePath <folder>. See docs/RENDERING.md."
    exit 2
}


Write-Host "Native runtime complete in $targetDir" -ForegroundColor Green
