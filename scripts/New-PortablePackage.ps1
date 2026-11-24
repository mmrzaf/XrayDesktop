#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [Parameter(Mandatory)]
    [string] $ExpectedVersion,
    [Parameter(Mandatory)]
    [string] $CoreDirectory,
    [Parameter(Mandatory)]
    [string] $ChecksExecutable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrayDistribution.psm1') -Force
$root = Split-Path -Parent $PSScriptRoot
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$core = (Resolve-Path -LiteralPath $CoreDirectory).Path
$checks = (Resolve-Path -LiteralPath $ChecksExecutable).Path
$xray = Get-XrayDistributionMetadata
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
[void] (New-Item -ItemType Directory -Path $payload -Force)

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-PayloadFile([string] $Source, [string] $Name) {
    $destination = Join-Path $payload $Name
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent)) {
        [void] (New-Item -ItemType Directory -Path $parent -Force)
    }
    Copy-Item -LiteralPath $Source -Destination $destination
}

function Assert-CoreVersion([string] $Path, [string] $Version) {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Path
    $start.WorkingDirectory = [System.IO.Path]::GetTempPath()
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $start.ArgumentList.Add('version')

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'Could not launch the packaged Xray executable.'
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            [void] $process.WaitForExit(5000)
            throw 'Packaged Xray version command timed out.'
        }
        $out = $stdout.GetAwaiter().GetResult().Trim()
        $err = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne 0) {
            throw "Packaged Xray version command exited with code $($process.ExitCode): $out $err"
        }
        if ($out -notmatch ('(?m)^Xray\s+v?' + [regex]::Escape($Version) + '(?:\s|$)')) {
            throw "Packaged Xray does not report v${Version}: $out"
        }
        Write-Host "PASS: packaged xray.exe reports v$Version"
    } finally {
        $process.Dispose()
    }
}

try {
    foreach ($entry in $xray.PayloadSha256.GetEnumerator()) {
        $source = Join-Path $core $entry.Key
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Verified Xray distribution is missing $($entry.Key)."
        }
        $sourceHash = Get-Sha256 $source
        if ($sourceHash -cne $entry.Value) {
            throw "Xray distribution file $($entry.Key) does not match the pinned payload: expected $($entry.Value); got $sourceHash."
        }
    }

    $licenseFiles = @(
        'Xray-core-LICENSE.txt',
        'Xray-rules-dat-LICENSE.txt',
        'IPinfo-Lite-CC-BY-SA-4.0.txt',
        'domain-list-community-MIT.txt'
    )
    $sources = [ordered] @{
        'XrayDesktop.exe' = Join-Path $publish 'XrayDesktop.exe'
        'xray.exe' = Join-Path $core 'xray.exe'
        'geoip.dat' = Join-Path $core 'geoip.dat'
        'geosite.dat' = Join-Path $core 'geosite.dat'
        'README.txt' = Join-Path $root 'docs/portable-readme.txt'
        'THIRD-PARTY-NOTICES.txt' = Join-Path $root 'docs/THIRD-PARTY-NOTICES.txt'
    }
    foreach ($name in $licenseFiles) {
        $sources["licenses/$name"] = Join-Path $root "licenses/$name"
    }
    $sources['examples/direct-test.json'] = Join-Path $root 'examples/direct-test.json'
    $payloadFiles = @($sources.Keys)
    foreach ($name in $payloadFiles) {
        if (-not (Test-Path -LiteralPath $sources[$name] -PathType Leaf)) {
            throw "Package source is missing $name."
        }
        Add-PayloadFile $sources[$name] $name
        if ((Get-Sha256 (Join-Path $payload $name)) -cne (Get-Sha256 $sources[$name])) {
            throw "Copied package file does not match its source: $name"
        }
    }

    $archiveNames = @()
    foreach ($name in $payloadFiles) {
        if ($name -ceq 'THIRD-PARTY-NOTICES.txt') { $archiveNames += 'SHA256SUMS.txt' }
        $archiveNames += $name
    }

    $manifestLines = foreach ($name in $payloadFiles) {
        "$(Get-Sha256 (Join-Path $payload $name))  $name"
    }
    [System.IO.File]::WriteAllText((Join-Path $payload 'SHA256SUMS.txt'), (($manifestLines -join "`n") + "`n"), [System.Text.UTF8Encoding]::new($false))

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
        foreach ($entry in $zip.Entries) {
            if ($entry.Length -le 0) { throw "ZIP entry is empty: $($entry.FullName)" }
        }
    } finally {
        $zip.Dispose()
    }

    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extract)
    $expectedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in $archiveNames) { [void] $expectedNames.Add($name) }
    $actualFiles = @(Get-ChildItem -LiteralPath $extract -Recurse -File)
    if ($actualFiles.Count -ne $expectedNames.Count) {
        throw "Extracted package contains $($actualFiles.Count) files; expected $($expectedNames.Count)."
    }
    foreach ($file in $actualFiles) {
        $name = [System.IO.Path]::GetRelativePath($extract, $file.FullName).Replace('\', '/')
        if (-not $expectedNames.Contains($name)) {
            throw "Unexpected extracted package file: $name"
        }
    }

    $manifestText = [System.IO.File]::ReadAllText((Join-Path $extract 'SHA256SUMS.txt'))
    $manifestNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($line in ($manifestText -split "`n" | Where-Object { $_ -ne '' })) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Invalid package manifest line: $line"
        }
        $name = $Matches[2]
        $manifestHash = $Matches[1]
        if (-not $expectedNames.Contains($name) -or $name -ceq 'SHA256SUMS.txt') {
            throw "Package manifest covers an unexpected file: $name"
        }
        if (-not $manifestNames.Add($name)) {
            throw "Package manifest repeats $name."
        }
        if ((Get-Sha256 (Join-Path $extract $name)) -cne $manifestHash) {
            throw "Extracted package checksum verification failed: $name"
        }
    }
    $payloadSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($name in $payloadFiles) { [void] $payloadSet.Add($name) }
    if (-not $manifestNames.SetEquals($payloadSet)) {
        throw 'Package manifest does not cover every payload file.'
    }

    foreach ($entry in $xray.PayloadSha256.GetEnumerator()) {
        if ((Get-Sha256 (Join-Path $extract $entry.Key)) -cne $entry.Value) {
            throw "Packaged $($entry.Key) is not byte-identical to the verified Xray distribution."
        }
    }

    Assert-CoreVersion (Join-Path $extract 'xray.exe') $xray.Version
    & $checks '--check-core' $extract
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled Xray integration check failed with exit code $LASTEXITCODE."
    }

    & (Join-Path $PSScriptRoot 'Test-PublishedApp.ps1') -Executable (Join-Path $extract 'XrayDesktop.exe') -ExpectedVersion $ExpectedVersion
    [System.IO.File]::WriteAllText((Join-Path $output "$archiveName.sha256"), "$(Get-Sha256 $zipPath)  $archiveName`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "Verified portable package: $zipPath"
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
