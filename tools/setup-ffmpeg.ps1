# Downloads a pinned FFmpeg 8.1 LGPL shared build (decode-only needs; no GPL x264/x265)
# into third_party\ffmpeg and verifies its SHA-256.
# Size: ~76 MB download, ~200-250 MB extracted. No admin rights, no global changes.
#
# Pinned from https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-11-04-12-51
# Hash taken from that release's checksums.sha256 on 2026-11-05.

$ErrorActionPreference = 'Stop'

$Release = 'autobuild-2026-11-04-12-51'
$Asset   = 'ffmpeg-n8.1.3-34-ga35157abb9-win64-lgpl-shared-8.1'
$Url     = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Release/$Asset.zip"
$Sha256  = '1b32a03bcc71e7db008484060dd1f5734369b14e4f5fea88368e96be35b7288d'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Downloads   = Join-Path $ProjectRoot '.local\downloads'
$Target      = Join-Path $ProjectRoot 'third_party\ffmpeg'
$Zip         = Join-Path $Downloads "$Asset.zip"

if (Test-Path (Join-Path $Target 'bin\avcodec-*.dll')) {
    Write-Host "[setup-ffmpeg] FFmpeg already present in $Target"
    return
}

New-Item -ItemType Directory -Force $Downloads | Out-Null

if (-not (Test-Path $Zip)) {
    Write-Host "[setup-ffmpeg] Downloading $Url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $Zip -UseBasicParsing
}

$actual = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLowerInvariant()
if ($actual -ne $Sha256) {
    Remove-Item $Zip -Force
    throw "[setup-ffmpeg] SHA-256 mismatch. Expected $Sha256 got $actual. Download deleted."
}
Write-Host '[setup-ffmpeg] SHA-256 verified.'

$Staging = Join-Path $Downloads 'ffmpeg-extract'
if (Test-Path $Staging) { Remove-Item -Recurse -Force $Staging }
Expand-Archive -Path $Zip -DestinationPath $Staging -Force

if (Test-Path $Target) { Remove-Item -Recurse -Force $Target }
New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
Move-Item (Join-Path $Staging $Asset) $Target
Remove-Item -Recurse -Force $Staging
Remove-Item $Zip -Force   # not needed once installed; run again to re-download

Write-Host "[setup-ffmpeg] Installed FFmpeg to $Target (DLLs in bin\)."
Write-Host '[setup-ffmpeg] The Windows client build copies these DLLs into its output ffmpeg\ folder.'
