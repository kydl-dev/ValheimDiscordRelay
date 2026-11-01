param(
    [string]$Version = "1.6.0",
    # SHA-256 of libwebp-1.6.0-windows-x64.zip as downloaded from Google (pinned 2026-11-01). Empty = only print it.
    [string]$ExpectedSha256 = "48886F506B21F62E4661F0F4CBFCA19800897C385128E8902542D29A950C93F1"
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

    $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash
    Write-Host "SHA-256 of the download: $actual"
    if ($ExpectedSha256 -and ($actual -ne $ExpectedSha256)) {
        Remove-Item $zip -Force
        throw "SHA-256 of the libwebp archive does not match (expected $ExpectedSha256, got $actual): the download was refused."
    }

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
