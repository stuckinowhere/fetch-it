# Build a per-user Windows installer that already contains the .NET runtime and tools.
# Output: artifacts\fetch-it-v<version>-win-x64-setup.exe

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot\..

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Install the .NET 8 SDK, then run this again:"
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0"
    exit 1
}

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    $fromPath = Get-Command iscc -ErrorAction SilentlyContinue
    if ($fromPath) { return $fromPath.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host "Inno Setup 6 is required to compile the installer."
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "Installing JRSoftware.InnoSetup..."
        winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
        $iscc = Find-Iscc
    }
}

if (-not $iscc) {
    Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php then re-run."
    exit 1
}

$csproj = [xml](Get-Content .\FetchIt.csproj)
$version = $csproj.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { $version = "1.0.0" }

$out = Join-Path $PWD "publish\win-x64"
dotnet publish .\FetchIt.csproj -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=none `
    /p:Version=$version `
    -o $out

& .\scripts\Get-Tools.ps1 -OutDir $out
Copy-Item .\Assets\fetchit.ico (Join-Path $out "FetchIt.ico") -Force
New-Item -ItemType Directory -Force -Path .\artifacts | Out-Null

& $iscc `
    "/DMyAppVersion=$version" `
    "/DPublishDir=$($out.Replace('\','/'))" `
    .\installer\fetch-it.iss

$setup = Join-Path $PWD "artifacts\fetch-it-v$version-win-x64-setup.exe"
if (-not (Test-Path $setup)) {
    throw "Installer was not created: $setup"
}

Write-Host ""
Write-Host "Built: $setup"
Write-Host "This setup includes the .NET runtime. The user does not install the SDK."
