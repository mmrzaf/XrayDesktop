#Requires -Version 7.0
Set-StrictMode -Version Latest

# Single source of truth for the bundled Xray distribution. The digest was
# independently recorded from ScoopInstaller/Main bucket/xray.json for 26.11.9.
# Download only from the official XTLS release, never from the checksum source.
$script:XrayVersion = '26.11.9'
$script:XrayAsset = 'Xray-windows-64.zip'
$script:XrayArchiveSha256 = '1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d'

# SHA-256 of the packaged runtime files, taken from the pinned archive above.
# geodata files come from XTLS/Xray-rules-dat and are bundled by Xray's release
# automation; see THIRD-PARTY-NOTICES.txt for their licensing and provenance.
$script:XrayPayloadSha256 = [ordered] @{
    'xray.exe'    = '1ca7a2cf61ba2c5d20b21a80d405b57933939751081cb289f07bd8ddf7cc3185'
    'geoip.dat'   = '283119b5c7679d3cc9a750e1b6da693ecfd42e5d5036b57f0c2d0505f76e36f4'
    'geosite.dat' = 'daaae9760ab5e687845817967257a6e9883d42ac9c09f4b774a32bd5cb39b4d7'
}

function Get-XrayDistributionMetadata {
    $payload = [ordered] @{}
    foreach ($entry in $script:XrayPayloadSha256.GetEnumerator()) {
        $payload[$entry.Key] = $entry.Value
    }
    [pscustomobject] @{
        Version = $script:XrayVersion
        Asset = $script:XrayAsset
        ArchiveSha256 = $script:XrayArchiveSha256
        PayloadSha256 = $payload
    }
}

function Get-XrayDistribution {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $OutputDirectory
    )

    if (Test-Path -LiteralPath $OutputDirectory) {
        throw 'Xray distribution directory must not already exist.'
    }
    [void] (New-Item -ItemType Directory -Path $OutputDirectory)
    $archive = Join-Path $OutputDirectory $script:XrayAsset
    $url = "https://github.com/XTLS/Xray-core/releases/download/v$($script:XrayVersion)/$($script:XrayAsset)"
    try {
        Invoke-WebRequest -Uri $url -OutFile $archive -TimeoutSec 180 -MaximumRedirection 5
        $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $script:XrayArchiveSha256) {
            throw "Pinned Xray archive hash mismatch: expected $($script:XrayArchiveSha256); got $actualHash."
        }
        $extract = Join-Path $OutputDirectory 'core'
        Expand-Archive -LiteralPath $archive -DestinationPath $extract
        foreach ($entry in $script:XrayPayloadSha256.GetEnumerator()) {
            $path = Join-Path $extract $entry.Key
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Verified Xray archive contains no $($entry.Key)."
            }
            $fileHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($fileHash -cne $entry.Value) {
                throw "Pinned Xray payload hash mismatch for $($entry.Key): expected $($entry.Value); got $fileHash."
            }
        }
        Write-Host "Verified Xray v$($script:XrayVersion) ($actualHash)."
        [pscustomobject] @{
            Directory = $extract
            Version = $script:XrayVersion
            ArchiveSha256 = $actualHash
        }
    } finally {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    }
}

Export-ModuleMember -Function Get-XrayDistribution, Get-XrayDistributionMetadata
