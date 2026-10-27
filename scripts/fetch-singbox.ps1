# Downloads the sing-box core into src/ProxyManager/core (copied next to the app exe).
# The archive is verified against a pinned SHA-256: when bumping the version, take the new
# digest from the GitHub release (the "sha256:..." shown next to the asset).
param(
    [string]$Version = "1.14.4",
    [string]$Sha256 = "99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$coreDir = Join-Path $root "src\ProxyManager\core"
$versionFile = Join-Path $coreDir "VERSION"

if (-not $Force -and (Test-Path "$coreDir\sing-box.exe") -and (Test-Path $versionFile) -and
    ((Get-Content $versionFile -Raw).Trim() -eq $Version)) {
    Write-Host "sing-box $Version is already in $coreDir"
    return
}

$name = "sing-box-$Version-windows-amd64"
$url = "https://github.com/SagerNet/sing-box/releases/download/v$Version/$name.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("singbox-" + [Guid]::NewGuid())

New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile "$tmp\core.zip" -UseBasicParsing

    $actual = (Get-FileHash "$tmp\core.zip" -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        throw "SHA-256 mismatch for $name.zip: expected $Sha256, got $actual"
    }

    Expand-Archive "$tmp\core.zip" -DestinationPath $tmp
    if (Test-Path $coreDir) { Remove-Item -Recurse -Force $coreDir }
    New-Item -ItemType Directory -Force $coreDir | Out-Null
    Get-ChildItem "$tmp\$name" -File | Copy-Item -Destination $coreDir
    Set-Content -Path $versionFile -Value $Version -NoNewline -Encoding ascii
    Write-Host "sing-box $Version -> $coreDir"
}
finally {
    Remove-Item -Recurse -Force $tmp
}
