param(
    [string]$Version = "1.6.0"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$embedded = Join-Path $root "EmbeddedWebP"
$temp = Join-Path $root "webp-download"
$zip = Join-Path $temp "libwebp-$Version-windows-x64.zip"

New-Item -ItemType Directory -Force -Path $embedded | Out-Null
New-Item -ItemType Directory -Force -Path $temp | Out-Null

$required = Join-Path $embedded "img2webp.exe"

if (-not (Test-Path $required)) {
    # Use the official WebP/Google Windows x64 binary archive.
    # The official archive contains img2webp.exe under bin\.
    $url = "https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-$Version-windows-x64.zip"

    Write-Host "Downloading official libwebp $Version Windows x64 tools..."
    Write-Host "Source: $url"

    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    Invoke-WebRequest -Uri $url -OutFile $zip

    $extract = Join-Path $temp "extract"
    if (Test-Path $extract) {
        Remove-Item $extract -Recurse -Force
    }

    Expand-Archive -Path $zip -DestinationPath $extract -Force

    $img = Get-ChildItem $extract -Recurse -Filter "img2webp.exe" |
        Select-Object -First 1

    if (-not $img) {
        throw "img2webp.exe was not found in the official libwebp Windows archive."
    }

    $bin = $img.Directory

    # img2webp may depend on libwebp DLLs shipped beside it, so embed
    # the executable and every DLL from its bin directory.
    Get-ChildItem $bin -File |
        Where-Object {
            $_.Name -eq "img2webp.exe" -or $_.Extension -ieq ".dll"
        } |
        Copy-Item -Destination $embedded -Force
}

if (-not (Test-Path $required)) {
    throw "WebP encoder preparation failed: $required was not created."
}

Write-Host "Embedded WebP tools ready in $embedded"
