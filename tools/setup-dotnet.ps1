# Downloads the pinned .NET SDK into .local\dotnet and verifies its SHA-512.
# Size: ~287 MB download, ~800 MB extracted. No admin rights, no global changes.
#
# Pinned from https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json
# (.NET 10.0.14 release, SDK 10.0.403, win-x64 zip).

$ErrorActionPreference = 'Stop'

$SdkVersion = '10.0.403'
$Url        = "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SdkVersion/dotnet-sdk-$SdkVersion-win-x64.zip"
$Sha512     = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Downloads   = Join-Path $ProjectRoot '.local\downloads'
$Target      = Join-Path $ProjectRoot '.local\dotnet'
$Zip         = Join-Path $Downloads "dotnet-sdk-$SdkVersion-win-x64.zip"

if (Test-Path (Join-Path $Target "sdk\$SdkVersion")) {
    Write-Host "[setup-dotnet] SDK $SdkVersion already present in $Target"
    return
}

New-Item -ItemType Directory -Force $Downloads | Out-Null

if (-not (Test-Path $Zip)) {
    Write-Host "[setup-dotnet] Downloading $Url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $Zip -UseBasicParsing
}

$actual = (Get-FileHash -Algorithm SHA512 $Zip).Hash.ToLowerInvariant()
if ($actual -ne $Sha512) {
    Remove-Item $Zip -Force
    throw "[setup-dotnet] SHA-512 mismatch. Expected $Sha512 got $actual. Download deleted."
}
Write-Host '[setup-dotnet] SHA-512 verified.'

New-Item -ItemType Directory -Force $Target | Out-Null
Expand-Archive -Path $Zip -DestinationPath $Target -Force
Remove-Item $Zip -Force   # not needed once extracted (~300 MB); run again to re-download
Write-Host "[setup-dotnet] Installed SDK $SdkVersion to $Target"
Write-Host '[setup-dotnet] Next: . .\tools\env.ps1 ; dotnet --info'
