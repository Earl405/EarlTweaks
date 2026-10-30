@echo off
title LiveBoost - build
cd /d "%~dp0"

set HAVE_SDK=0
for /f %%i in ('dotnet --list-sdks 2^>nul ^| find /c "8."') do if not "%%i"=="0" set HAVE_SDK=1

if "%HAVE_SDK%"=="0" (
  echo The .NET 8 SDK is not installed ^(only the runtime, or nothing^).
  echo Trying to install it with winget...
  winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
  echo.
  echo When the install finishes, CLOSE this window and double-click BUILD-AND-RUN.bat again.
  echo If winget is missing, install the SDK from https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b
)

echo Building LiveBoost (needs internet the first time)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0LiveBoost-App"

if not exist "%~dp0LiveBoost-App\LiveBoost.exe" (
  echo.
  echo BUILD FAILED. Copy the red text above and send it to Claude.
  pause
  exit /b
)

set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if exist "%ISCC%" "%ISCC%" setup.iss

echo.
echo Done. Starting LiveBoost...
start "" "%~dp0LiveBoost-App\LiveBoost.exe"
echo The app is in the LiveBoost-App folder. If Inno Setup is installed, the installer is in the installer folder.
echo For FPS and CPU temperature, right-click LiveBoost.exe and choose Run as administrator.
pause
