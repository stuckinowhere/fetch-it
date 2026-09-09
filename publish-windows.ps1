# Build FetchIt.exe for this Windows PC, then launch it.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Install the .NET 8 SDK, then run this again:"
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0"
    Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

$out = Join-Path $PSScriptRoot "publish\win-x64"
dotnet publish .\FetchIt.csproj -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=none `
    -o $out

& (Join-Path $PSScriptRoot "scripts\Get-Tools.ps1") -OutDir $out
Copy-Item (Join-Path $PSScriptRoot "Assets\fetchit.ico") (Join-Path $out "FetchIt.ico") -Force

$exe = Join-Path $out "FetchIt.exe"
Write-Host ""
Write-Host "Built: $exe"
Write-Host "Starting fetch it..."
Start-Process $exe
