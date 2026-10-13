@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1

where dotnet >nul 2>&1
if errorlevel 1 goto missing_sdk
dotnet --list-sdks 2>nul | findstr /r /c:"^[0-9]" >nul
if errorlevel 1 goto missing_sdk

echo Building Keyloom...
dotnet build "Keyloom.csproj" -c Release --nologo --verbosity quiet
if errorlevel 1 goto build_failed

if /i "%~1"=="--build-only" goto success
start "" "%~dp0bin\Release\net10.0-windows\Keyloom.exe"
if errorlevel 1 goto launch_failed

:success
popd
exit /b 0

:missing_sdk
echo Install the .NET 10 SDK or newer, then run this file again.
echo https://dotnet.microsoft.com/download/dotnet/10.0
goto failed

:build_failed
echo Build failed. Close Keyloom from its tray menu and try again.
echo This project requires the .NET 10 SDK or newer.
goto failed

:launch_failed
echo Keyloom could not be started. Check the build output above.

:failed
pause
popd
exit /b 1
