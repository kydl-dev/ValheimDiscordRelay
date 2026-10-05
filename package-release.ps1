param([string]$Version = "1.3.3")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "ValheimDiscordRelay.csproj"
$prepareWebP = Join-Path $root "Build\prepare-webp.ps1"
$dist = Join-Path $root "dist"
$pkg = Join-Path $dist "ValheimDiscordRelay-$Version"
$zip = Join-Path $dist "ValheimDiscordRelay-$Version.zip"

# Prepare the embedded Windows x64 WebP animation tool used by the client death recorder.
if (Test-Path $prepareWebP) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $prepareWebP
    if ($LASTEXITCODE -ne 0) { throw "WebP tool preparation failed." }
}

# Locate MSBuild even when this script is launched from a normal PowerShell window.
$msbuild = $null
$msbuildCommand = Get-Command msbuild.exe -ErrorAction SilentlyContinue
if ($msbuildCommand) {
    $msbuild = $msbuildCommand.Source
}

if (-not $msbuild) {
    $vswhereCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"),
        (Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe")
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($vswhere in $vswhereCandidates) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" 2>$null | Select-Object -First 1
        if ($found -and (Test-Path $found)) {
            $msbuild = $found
            break
        }
    }
}

if (-not $msbuild) {
    $commonPaths = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2026\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2026\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2026\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
    )

    $msbuild = $commonPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $msbuild) {
    throw @"
MSBuild.exe was not found.

If Visual Studio is installed, open "Developer PowerShell for Visual Studio" and run this script again.
If it is not installed, install Visual Studio or the Visual Studio Build Tools with the MSBuild component.
"@
}

Write-Host "Using MSBuild: $msbuild"

if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

& $msbuild $project /m /p:Configuration=Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dll = Join-Path $root "bin/Release/ValheimDiscordRelay.dll"
if (-not (Test-Path $dll)) { throw "Build completed, but ValheimDiscordRelay.dll was not found at $dll" }

New-Item -ItemType Directory -Force -Path (Join-Path $pkg "BepInEx/plugins") | Out-Null
Copy-Item $dll (Join-Path $pkg "BepInEx/plugins/ValheimDiscordRelay.dll")
Copy-Item (Join-Path $root "manifest.json") $pkg
Copy-Item (Join-Path $root "README.md") $pkg
Copy-Item (Join-Path $root "CHANGELOG.md") $pkg
Copy-Item (Join-Path $root "icon.png") $pkg

Compress-Archive -Path (Join-Path $pkg "*") -DestinationPath $zip -Force
Write-Host "Package ready: $zip"
