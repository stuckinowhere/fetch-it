# Download yt-dlp, gallery-dl, and ffmpeg into a folder. Not committed to git.

param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-Tool([string] $Url, [string] $Dest) {
    if ((Test-Path $Dest) -and ((Get-Item $Dest).Length -gt 1024)) {
        Write-Host "Have $(Split-Path $Dest -Leaf)"
        return
    }
    Write-Host "Get $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing
}

Get-Tool "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" (Join-Path $OutDir "yt-dlp.exe")
Get-Tool "https://github.com/mikf/gallery-dl/releases/latest/download/gallery-dl.exe" (Join-Path $OutDir "gallery-dl.exe")

$ffmpeg = Join-Path $OutDir "ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) {
    $zip = Join-Path $OutDir "ffmpeg.zip"
    Get-Tool "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" $zip
    $tmp = Join-Path $OutDir "ffmpeg-unpack"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    Get-ChildItem $tmp -Recurse -Filter ffmpeg.exe | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName $ffmpeg -Force }
    Get-ChildItem $tmp -Recurse -Filter ffprobe.exe | Select-Object -First 1 | ForEach-Object { Copy-Item $_.FullName (Join-Path $OutDir "ffprobe.exe") -Force }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}

Write-Host "Tools in $OutDir"
