[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "3.3.1",
    [string]$OutputDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $root) { $root = Get-Location }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host " FishyNet MSI Installer Build Pipeline " -ForegroundColor Cyan
Write-Host " Version: $Version | Configuration: $Configuration " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Ensure output directory exists
$artifactsPath = Join-Path $root $OutputDir
if (-not (Test-Path $artifactsPath)) {
    New-Item -ItemType Directory -Path $artifactsPath -Force | Out-Null
}

$publishDir = Join-Path $root "FishyNet\bin\publish-win-x64"
$setupDir = Join-Path $root "FishyNet.Setup"
$msiFileName = "FishyNet-$Version-win-x64.msi"
$msiPath = Join-Path $artifactsPath $msiFileName

# 1. Publish standalone self-contained win-x64 binaries (complete with all WPF native runtime libraries)
Write-Host "`n[1/5] Publishing FishyNet (win-x64, self-contained)..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish (Join-Path $root "FishyNet\FishyNet.csproj") `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 2. Ensure .dotnet/tools is in PATH and WiX CLI is available
Write-Host "`n[2/5] Checking WiX toolset..." -ForegroundColor Yellow
$dotnetToolsDir = Join-Path $env:USERPROFILE ".dotnet\tools"
if (($env:PATH -split ';') -notcontains $dotnetToolsDir) {
    $env:PATH = "$dotnetToolsDir;$env:PATH"
}

$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    Write-Host "WiX CLI not found. Installing dotnet tool 'wix' globally..." -ForegroundColor Cyan
    dotnet tool install -g wix
    if (($env:PATH -split ';') -notcontains $dotnetToolsDir) {
        $env:PATH = "$dotnetToolsDir;$env:PATH"
    }
    $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
}

# Accept WiX EULA if using WiX v7+
try {
    & wix eula accept wix7 2>$null
    $global:LASTEXITCODE = 0
} catch {}

# 3. Ensure WixToolset.UI.wixext is installed
Write-Host "`n[3/5] Verifying WiX UI extension..." -ForegroundColor Yellow
& wix extension add -g WixToolset.UI.wixext
if ($LASTEXITCODE -ne 0) {
    Write-Host "WiX extension add returned $LASTEXITCODE (extension may already be installed)." -ForegroundColor Gray
    $global:LASTEXITCODE = 0
}

# 4. Build MSI package
Write-Host "`n[4/5] Building MSI package with WiX..." -ForegroundColor Yellow
& wix build `
    -arch x64 `
    -ext WixToolset.UI.wixext `
    -d SetupDir="$setupDir" `
    -d PublishDir="$publishDir" `
    -d Version="$Version" `
    -o "$msiPath" `
    (Join-Path $setupDir "Package.wxs")

if ($LASTEXITCODE -ne 0) {
    throw "wix build failed with exit code $LASTEXITCODE"
}

# 5. Validate MSI using ICE validation
Write-Host "`n[5/5] Validating MSI package (ICE suite)..." -ForegroundColor Yellow
& wix msi validate "$msiPath"
if ($LASTEXITCODE -ne 0) {
    throw "wix msi validate reported validation errors"
}

# 6. Generate SHA256 checksum
$hash = (Get-FileHash -Path $msiPath -Algorithm SHA256).Hash
$hashFile = "$msiPath.sha256"
Set-Content -Path $hashFile -Value "$hash *$msiFileName"

Write-Host "`n========================================" -ForegroundColor Green
Write-Host " MSI Build Completed Successfully! " -ForegroundColor Green
Write-Host " MSI:      $msiPath" -ForegroundColor Green
Write-Host " Size:     $((Get-Item $msiPath).Length) bytes" -ForegroundColor Green
Write-Host " SHA256:   $hash" -ForegroundColor Green
Write-Host " Checksum: $hashFile" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
