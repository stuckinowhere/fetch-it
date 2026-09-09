# Run fetch it on this Windows PC (dev mode).
# For a single exe + bundled tools, use publish-windows.ps1.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Install the .NET 8 SDK, then run this again:"
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0"
    Start-Process "https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

dotnet --list-sdks
dotnet run --project .\FetchIt.csproj -c Release
