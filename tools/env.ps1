# Dot-source this in a PowerShell session before building:
#     . .\tools\env.ps1
#
# Points .NET and NuGet at project-local folders for THIS SESSION ONLY.
# Nothing is written to the registry, the user profile, or the global PATH.

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Local = Join-Path $ProjectRoot '.local'

$env:DOTNET_ROOT                       = Join-Path $Local 'dotnet'
$env:DOTNET_CLI_HOME                   = Join-Path $Local 'dotnet-home'
$env:NUGET_PACKAGES                    = Join-Path $Local 'nuget\packages'
$env:NUGET_HTTP_CACHE_PATH             = Join-Path $Local 'nuget\http-cache'
$env:NUGET_PLUGINS_CACHE_PATH          = Join-Path $Local 'nuget\plugins-cache'
$env:NUGET_SCRATCH                     = Join-Path $Local 'nuget\scratch'
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = '1'
$env:DOTNET_NOLOGO                     = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH   = 'false'
$env:DOTNET_MULTILEVEL_LOOKUP          = '0'

# Prepend the local SDK to PATH for this process only.
if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $env:DOTNET_ROOT })) {
    $env:PATH = "$($env:DOTNET_ROOT);$($env:PATH)"
}

$dotnetExe = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (Test-Path $dotnetExe) {
    Write-Host "[env] Using project-local .NET SDK: $dotnetExe"
} else {
    Write-Warning "[env] Local .NET SDK not found at $($env:DOTNET_ROOT). Run .\tools\setup-dotnet.ps1 first."
}
