# Removes everything Macstream added on this Windows PC.
#
#   .\tools\uninstall-windows.ps1            # remove downloads, SDK, caches, builds, logs; keep source code
#   .\tools\uninstall-windows.ps1 -All       # remove the whole project folder too
#   .\tools\uninstall-windows.ps1 -DryRun    # only show what would be removed
#
# Macstream keeps its .NET SDK, NuGet packages, FFmpeg, build output and logs inside the
# project folder (settings and the pairing key in .local\), and changes nothing in the
# registry, PATH, firewall or user profile. Windows itself remembers the tray icon and a
# taskbar pin; those entries are removed too (only ones pointing into this folder).
#
# Not touched: your GitHub repos (see the note printed at the end), and shared system
# folders that other programs also use.

param(
    [switch]$All,
    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot

function Format-Size([long]$bytes) { '{0:N0} MB' -f ($bytes / 1MB) }
function Get-Size([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -Force -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($null -eq $sum) { 0 } else { [long]$sum }
}

# 1. What will be removed
if ($All) {
    $targets = @($ProjectRoot)
} else {
    $targets = @(
        '.local',                              # .NET SDK, NuGet packages, downloads
        'third_party',                         # FFmpeg DLLs
        'build',                               # build output
        'logs',                                # logs
        'Windows\MacstreamViewer\bin',
        'Windows\MacstreamViewer\obj',
        'NetworkTest\bin',
        'NetworkTest\obj',
        'Mac\MacstreamServer\.build'
    ) | ForEach-Object { Join-Path $ProjectRoot $_ } | Where-Object { Test-Path -LiteralPath $_ }
}

# Traces Windows itself keeps about the viewer (Macstream doesn't write them, but they point
# at it): the tray icon's entry under HKCU\Control Panel\NotifyIconSettings, and a taskbar pin
# if you pinned Macstream. Only entries whose program is inside this project folder.
$projectPrefix = $ProjectRoot.TrimEnd('\') + '\'
function Test-InProject([string]$path) {
    $path -and $path.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)
}
$trayKeys = @(Get-ChildItem -LiteralPath 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue |
    Where-Object { Test-InProject (Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue).ExecutablePath })
$pinDir = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$pins = @()
if (Test-Path -LiteralPath $pinDir) {
    $shell = New-Object -ComObject WScript.Shell
    $pins = @(Get-ChildItem -LiteralPath $pinDir -Filter '*.lnk' -ErrorAction SilentlyContinue |
        Where-Object { Test-InProject $shell.CreateShortcut($_.FullName).TargetPath })
}

Write-Host ''
Write-Host 'Macstream uninstaller (Windows)' -ForegroundColor Cyan
Write-Host "Project folder: $ProjectRoot"
Write-Host ''
$anything = ($targets.Count + $trayKeys.Count + $pins.Count) -gt 0
if (-not $anything) {
    Write-Host 'Nothing to remove. Already clean.' -ForegroundColor Green
} else {
    Write-Host 'Will remove:'
    $total = 0L
    foreach ($t in $targets) {
        $size = Get-Size $t
        $total += $size
        Write-Host ("  {0,10}  {1}" -f (Format-Size $size), $t)
    }
    if ($targets.Count -gt 0) { Write-Host ("  {0,10}  total" -f (Format-Size $total)) }
    foreach ($k in $trayKeys) { Write-Host "  tray icon entry  $($k.Name)" }
    foreach ($p in $pins) { Write-Host "  taskbar pin      $($p.FullName)" }
}
if (-not $All) {
    Write-Host ''
    Write-Host 'Source code is kept. Use -All to delete the whole project folder.'
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run: nothing was removed.' -ForegroundColor Yellow
    return
}

if ($anything) {
    if (-not $Yes) {
        Write-Host ''
        $answer = Read-Host 'Type YES to continue'
        if ($answer -cne 'YES') { Write-Host 'Cancelled. Nothing was removed.'; return }
    }

    # 2. Stop anything that could be holding files open
    Get-Process -Name 'Macstream' -ErrorAction SilentlyContinue | Stop-Process -Force
    $localDotnet = Join-Path $ProjectRoot '.local\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) {
        $env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.local\dotnet-home'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        & $localDotnet build-server shutdown *> $null
    }
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    # 3. Remove
    if ($All) { Set-Location (Split-Path -Parent $ProjectRoot) }
    foreach ($t in $targets) {
        try {
            Remove-Item -LiteralPath $t -Recurse -Force
            Write-Host "Removed $t" -ForegroundColor Green
        } catch {
            Write-Warning "Could not remove $t : $($_.Exception.Message)"
            Write-Warning 'Close any program using it (Explorer window, editor, terminal) and run this again.'
        }
    }
    foreach ($k in $trayKeys) {
        Remove-Item -LiteralPath $k.PSPath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Removed tray icon entry $($k.PSChildName)" -ForegroundColor Green
    }
    foreach ($p in $pins) {
        Remove-Item -LiteralPath $p.FullName -Force -ErrorAction SilentlyContinue
        Write-Host "Removed taskbar pin $($p.Name) (if a blank icon stays on the taskbar: right-click it > Unpin)" -ForegroundColor Green
    }
}

# 4. Things outside this PC that this script deliberately leaves alone
Write-Host ''
Write-Host 'Not removed automatically:' -ForegroundColor Cyan
Write-Host '  - Any copy of the code on GitHub or elsewhere online: delete it there if you want it gone.'
Write-Host '  - The Windows "Unidentified network" entry for the Ethernet cable is created by Windows'
Write-Host '    itself and goes away when you stop using the cable.'
Write-Host '  - Mac side: run ./tools/mac/uninstall.sh on the Mac.'
