# Builds release artifacts into dist/:
#   ProxyManager-<version>-setup.exe              (Inno Setup installer)
#   ProxyManager-<version>-win-x64-portable.zip   (unpack and run)
#
# Usage: .\scripts\build-release.ps1 [-Version 0.2.0] [-SkipTests]
param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\ProxyManager\ProxyManager.csproj"
$publishDir = Join-Path $root "artifacts\publish"
$distDir = Join-Path $root "dist"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "== Proxy Manager $Version" -ForegroundColor Cyan

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe exited with code $LASTEXITCODE" }
}

# 1. sing-box core (no-op when the pinned version is already there)
& (Join-Path $PSScriptRoot "fetch-singbox.ps1")

# 2. Tests
if (-not $SkipTests) {
    Write-Host "== Tests" -ForegroundColor Cyan
    Invoke-Checked dotnet @("test", (Join-Path $root "ProxyManager.sln"), "-c", "Release", "--nologo")
}

# 3. Self-contained single-file publish (users don't need .NET installed)
Write-Host "== Publish" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
Invoke-Checked dotnet @(
    "publish", $project, "-c", "Release", "-r", "win-x64", "--self-contained", "--nologo",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-p:Version=$Version",
    "-o", $publishDir)

# Code signing goes here (before packaging), e.g.:
#   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a "$publishDir\ProxyManager.exe"

New-Item -ItemType Directory -Force $distDir | Out-Null

# 4. Portable zip (files inside a "ProxyManager" folder)
Write-Host "== Portable zip" -ForegroundColor Cyan
$zip = Join-Path $distDir "ProxyManager-$Version-win-x64-portable.zip"
$staging = Join-Path $root "artifacts\portable\ProxyManager"
if (Test-Path (Split-Path $staging)) { Remove-Item -Recurse -Force (Split-Path $staging) }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item "$publishDir\*" $staging -Recurse
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $staging -DestinationPath $zip -CompressionLevel Optimal

# 5. Installer
Write-Host "== Installer" -ForegroundColor Cyan
$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Invoke-Checked $iscc @("/Q", "/DAppVersion=$Version", "/DPublishDir=$publishDir", (Join-Path $root "installer\ProxyManager.iss"))
}
else {
    Write-Warning "Inno Setup not found (winget install JRSoftware.InnoSetup) - installer skipped"
}

Write-Host "== Done" -ForegroundColor Green
Get-ChildItem $distDir -File | Where-Object Name -like "*$Version*" |
    Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize |
    Out-String | Write-Host
