# Build FetchIt.exe for this Windows PC, then launch it.

$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "scripts\Publish-WinX64.ps1") -Launch
