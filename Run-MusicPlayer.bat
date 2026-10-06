@echo off
setlocal
cd /d "%~dp0"

set "PROJECT=src\MusicPlayer.App\MusicPlayer.App.csproj"
set "APP_EXE=%~dp0src\MusicPlayer.App\bin\x64\Release\net10.0-windows10.0.26100.0\BrackenVale.exe"

if not exist "%PROJECT%" (
    echo Could not find the Music Player project. Run this file from the repository checkout.
    goto :failed
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK was not found. Install the .NET 10 SDK, then run this file again.
    goto :failed
)

echo Closing any running Music Player window so an older copy cannot receive this launch...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; Get-Process -Name BrackenVale -ErrorAction SilentlyContinue | ForEach-Object { $p = $_; if ($p.MainWindowHandle -ne 0 -and -not $p.CloseMainWindow()) { throw 'Could not close a running Music Player window.' }; if (-not $p.WaitForExit(20000)) { throw 'Music Player did not close within 20 seconds. Close it and run this file again.' } }; exit 0"
if errorlevel 1 goto :failed

echo Building the latest source for Windows x64...
dotnet build "%PROJECT%" --configuration Release -p:Platform=x64
if errorlevel 1 goto :failed

if not exist "%APP_EXE%" (
    echo Build succeeded, but the expected executable was not created:
    echo "%APP_EXE%"
    goto :failed
)

echo Launching the executable from this checkout:
echo "%APP_EXE%"
echo Waiting for the app window to appear so a startup crash is reported here...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $p = Start-Process -FilePath '%APP_EXE%' -PassThru; $deadline = (Get-Date).AddSeconds(20); do { $p.Refresh(); if ($p.HasExited) { throw ('Music Player exited during startup with code ' + $p.ExitCode + '.') }; if ($p.MainWindowHandle -ne 0) { Write-Output 'Music Player window is ready.'; exit 0 }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline); throw 'Music Player process is running but its window did not appear within 20 seconds.'"
if errorlevel 1 goto :failed
exit /b 0

:failed
echo.
echo Music Player was not launched. Fix the message above and run this file again.
pause
exit /b 1
