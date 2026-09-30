# One win-x64 publish recipe: single-file FetchIt.exe, pinned tools, icon.
# Optional -Version / -Tag stamp the assembly; -Launch starts FetchIt.exe.

param(
    [string] $OutDir,
    [string] $Version,
    [string] $Tag,
    [switch] $Launch
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot "publish\win-x64"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Install the .NET 8 SDK, then run this again:"
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0"
    if ($Launch) {
        Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"
    }
    throw "The .NET 8 SDK is required."
}

$publishArgs = @(
    ".\FetchIt.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:DebugType=none",
    "-o", $OutDir
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "/p:Version=$Version"
}
if (-not [string]::IsNullOrWhiteSpace($Tag)) {
    $publishArgs += "/p:InformationalVersion=$Tag"
}

dotnet publish @publishArgs

& (Join-Path $PSScriptRoot "Get-Tools.ps1") -OutDir $OutDir
Copy-Item (Join-Path $repoRoot "Assets\fetchit.ico") (Join-Path $OutDir "FetchIt.ico") -Force

if ($Launch) {
    $exe = Join-Path $OutDir "FetchIt.exe"
    Write-Host ""
    Write-Host "Built: $exe"
    Write-Host "Starting fetch it..."
    Start-Process $exe
}
