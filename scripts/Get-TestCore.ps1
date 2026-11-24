#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Independent digest recorded from ScoopInstaller/Main bucket/xray.json for 26.11.9.
# Download only from the official XTLS release, never from the checksum source.
$version = '26.11.9'
$asset = 'Xray-windows-64.zip'
$expectedHash = '1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d'
$url = "https://github.com/XTLS/Xray-core/releases/download/v$version/$asset"
if (Test-Path -LiteralPath $OutputDirectory) {
    throw 'Test core directory must not already exist.'
}
[void] (New-Item -ItemType Directory -Path $OutputDirectory)
$archive = Join-Path $OutputDirectory $asset
try {
    Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 180 -MaximumRedirection 5
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Pinned Xray archive hash mismatch: expected $expectedHash; got $actualHash."
    }
    $extract = Join-Path $OutputDirectory 'core'
    Expand-Archive -LiteralPath $archive -DestinationPath $extract
    $core = Join-Path $extract 'xray.exe'
    if (-not (Test-Path -LiteralPath $core -PathType Leaf)) { throw 'Verified archive contains no xray.exe.' }
    Write-Host "Verified Xray v$version ($actualHash)."
    Write-Output $core
} finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
