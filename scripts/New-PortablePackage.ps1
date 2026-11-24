#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [Parameter(Mandatory)]
    [string] $ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (Test-Path -LiteralPath $OutputDirectory) {
    throw "Package output must not already exist: $OutputDirectory"
}
$entries = @(Get-ChildItem -LiteralPath $publish -Force)
if ($entries.Count -ne 1 -or $entries[0].PSIsContainer -or $entries[0].Name -cne 'XrayDesktop.exe') {
    throw "Expected one published executable; found: $($entries.Name -join ', ')"
}
if ($entries[0].Length -le 0) {
    throw 'Published executable is empty.'
}

[void] (New-Item -ItemType Directory -Path $OutputDirectory)
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('Xray Desktop package ' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$extract = Join-Path $work 'extracted package – test'
[void] (New-Item -ItemType Directory -Path (Join-Path $payload 'examples') -Force)

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

try {
    $files = @('README.txt', 'XrayDesktop.exe', 'examples/direct-test.json')
    Copy-Item -LiteralPath (Join-Path $publish 'XrayDesktop.exe') -Destination (Join-Path $payload 'XrayDesktop.exe')
    Copy-Item -LiteralPath (Join-Path $root 'docs/portable-readme.txt') -Destination (Join-Path $payload 'README.txt')
    Copy-Item -LiteralPath (Join-Path $root 'examples/direct-test.json') -Destination (Join-Path $payload 'examples/direct-test.json')
    $manifestLines = foreach ($name in $files) {
        "$(Get-Sha256 (Join-Path $payload $name))  $name"
    }
    [System.IO.File]::WriteAllText((Join-Path $payload 'SHA256SUMS.txt'), (($manifestLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

    $archiveNames = @('README.txt', 'SHA256SUMS.txt', 'XrayDesktop.exe', 'examples/direct-test.json')
    $archiveName = "XrayDesktop-$ExpectedVersion-win-x64.zip"
    $zipPath = Join-Path $output $archiveName
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $archiveNames) {
            $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            # Set the timestamp before opening the entry for writing.
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $source = [System.IO.File]::OpenRead((Join-Path $payload $name))
            try {
                $destination = $entry.Open()
                try {
                    $source.CopyTo($destination)
                } finally {
                    $destination.Dispose()
                }
            } finally {
                $source.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $actualNames = @($zip.Entries | ForEach-Object { $_.FullName })
        if (($actualNames -join "`n") -cne ($archiveNames -join "`n")) {
            throw "Unexpected ZIP entries: $($actualNames -join ', ')"
        }
    } finally {
        $zip.Dispose()
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extract)
    $extractedManifest = [System.IO.File]::ReadAllText((Join-Path $extract 'SHA256SUMS.txt'))
    $actualManifestLines = foreach ($name in $files) {
        "$(Get-Sha256 (Join-Path $extract $name))  $name"
    }
    if ($extractedManifest -cne (($actualManifestLines -join "`n") + "`n") -or
        ($actualManifestLines -join "`n") -cne ($manifestLines -join "`n")) {
        throw 'Extracted package checksum verification failed.'
    }

    & (Join-Path $PSScriptRoot 'Test-PublishedApp.ps1') -Executable (Join-Path $extract 'XrayDesktop.exe') -ExpectedVersion $ExpectedVersion
    [System.IO.File]::WriteAllText((Join-Path $output "$archiveName.sha256"), "$(Get-Sha256 $zipPath)  $archiveName`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Verified portable package: $zipPath"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
