# Download pinned yt-dlp, ffmpeg, and gallery-dl from scripts/tool-pins.json. Not committed to git.

param(
    [Parameter(Mandatory = $true)]
    [string] $OutDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$pinsPath = Join-Path $PSScriptRoot "tool-pins.json"
$pins = Get-Content -LiteralPath $pinsPath -Raw | ConvertFrom-Json
foreach ($pin in @($pins.ytDlp, $pins.ffmpegZip, $pins.galleryDlWheel)) {
    if (-not $pin -or -not $pin.url -or -not $pin.sha256) {
        throw "scripts/tool-pins.json is missing a url or sha256."
    }
}

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
        $actual = (Get-FileHash -Algorithm SHA256 -Path $tmp).Hash.ToLowerInvariant()
        $size = (Get-Item $tmp).Length
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "Hash mismatch for $(Split-Path $Dest -Leaf) ($size bytes). expected=$Sha actual=$actual"
    }
    Move-Item $tmp $Dest -Force
}

Get-Verified $pins.ytDlp.url (Join-Path $OutDir "yt-dlp.exe") $pins.ytDlp.sha256

$ffmpeg = Join-Path $OutDir "ffmpeg.exe"
if (-not (Test-Path $ffmpeg)) {
    $zip = Join-Path $OutDir "ffmpeg.zip"
    Get-Verified $pins.ffmpegZip.url $zip $pins.ffmpegZip.sha256
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
    Get-Verified $pins.galleryDlWheel.url $wheel $pins.galleryDlWheel.sha256
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
