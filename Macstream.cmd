@echo off
rem Opens the Macstream viewer (Windows client). Double-click this file.
rem It only builds and starts the app inside this folder; it installs and changes nothing else.
setlocal
set "ROOT=%~dp0"
set "OUT=%ROOT%Windows\MacstreamViewer\bin\Debug\net10.0-windows"
set "EXE=%OUT%\Macstream.exe"

rem Already open? Don't start a second copy.
tasklist /FI "IMAGENAME eq Macstream.exe" 2>nul | find /I "Macstream.exe" >nul
if not errorlevel 1 (
    echo Macstream is already open - look for it on the taskbar.
    timeout /t 3 >nul
    exit /b 0
)

rem Build when the app is missing or any of its source files is newer than the last build
rem (for example after a git pull). Otherwise start it straight away.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dll = '%OUT%\Macstream.dll';" ^
  "if (-not (Test-Path $dll)) { exit 1 };" ^
  "$built = (Get-Item $dll).LastWriteTime;" ^
  "$newer = Get-ChildItem '%ROOT%Windows\MacstreamViewer' -Recurse -File -Include *.cs,*.xaml,*.csproj,*.manifest,*.ico | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTime -gt $built };" ^
  "if ($newer) { exit 1 } else { exit 0 }"
if errorlevel 1 (
    echo Building Macstream, this takes a few seconds ^(about a minute the first time^)...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ". '%ROOT%tools\env.ps1'; dotnet build '%ROOT%Windows\MacstreamViewer\MacstreamViewer.csproj' -c Debug --nologo -v q; exit $LASTEXITCODE"
    if errorlevel 1 (
        echo Build failed. See the messages above.
        pause
        exit /b 1
    )
)

start "" "%EXE%"
exit /b 0
