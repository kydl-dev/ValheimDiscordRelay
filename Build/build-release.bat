@echo off
setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prepare-webp.ps1"
if errorlevel 1 exit /b 1

set "ROOT=%~dp0.."
set "PROJECT=%ROOT%\ValheimDiscordRelay.csproj"

where msbuild.exe >nul 2>&1
if errorlevel 1 (
    echo MSBuild.exe was not found in PATH.
    echo Open Developer PowerShell for Visual Studio or use package-release.ps1.
    exit /b 1
)

msbuild "%PROJECT%" /m /p:Configuration=Release
if errorlevel 1 exit /b 1

echo Build complete: %ROOT%\bin\Release\ValheimDiscordRelay.dll
exit /b 0
