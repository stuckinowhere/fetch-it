# Download pinned yt-dlp, ffmpeg, and gallery-dl (hashed). Not committed to git.

param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/download/2026.08.19/yt-dlp.exe"
$YtDlpSha = "66674953fe251b89f4d08c5d0e35e0728679bd67ab3d7d05c0562af101dd3e7a"
$FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-09-14-51/ffmpeg-N-126482-g903325e279-win64-gpl.zip"
$FfmpegSha = "6c60a0c17a02eab0ead59c4597e58268fa024b45ff08cd27b6d6be8e50fd2588"
$WheelUrl = "https://files.pythonhosted.org/packages/d6/6b/ac77fe9f7c050ca04de17174f5fc384ae104b009424b147089f0a8037272/gallery_dl-1.32.11-py3-none-any.whl"
$WheelSha = "67fcb941083defebcf0d075e6c0c0aab84a5d8ef23e927f34bf9b9860754958b"

function Test-Sha256([string] $Path, [string] $Expected) {
    if (-not (Test-Path $Path)) { return $false }
    $actual = (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
    return $actual -eq $Expected.ToLowerInvariant()
}

function Get-Verified([string] $Url, [string] $Dest, [string] $Sha) {
    if (Test-Sha256 $Dest $Sha) {
        Write-Host "Have $(Split-Path $Dest -Leaf)"
        return
    }
    Write-Host "Get $Url"
    $tmp = "$Dest.part"
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing
    if (-not (Test-Sha256 $tmp $Sha)) {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "Hash mismatch for $(Split-Path $Dest -Leaf)"
    }
    Move-Item $tmp $Dest -Force
}

Get-Verified $YtDlpUrl (Join-Path $OutDir "yt-dlp.exe") $YtDlpSha

$ffmpeg = Join-Path $OutDir "ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) {
    $zip = Join-Path $OutDir "ffmpeg.zip"
    Get-Verified $FfmpegUrl $zip $FfmpegSha
    $tmp = Join-Path $OutDir "ffmpeg-unpack"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName $ffmpeg -Force }
    Get-ChildItem $tmp -Recurse -Filter ffprobe.exe | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir "ffprobe.exe") -Force }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}

$wheel = Join-Path $OutDir "gallery-dl.whl"
$lib = Join-Path $OutDir "gallery-dl-lib"
try {
    Get-Verified $WheelUrl $wheel $WheelSha
    New-Item -ItemType Directory -Force -Path $lib | Out-Null
    $wheelZip = Join-Path $OutDir "gallery-dl.whl.zip"
    Copy-Item $wheel $wheelZip -Force
    Expand-Archive -Path $wheelZip -DestinationPath $lib -Force
    Remove-Item $wheelZip -Force -ErrorAction SilentlyContinue
}
catch {
    Write-Host "gallery-dl wheel skipped: $_"
}

Write-Host "Tools in $OutDir"
