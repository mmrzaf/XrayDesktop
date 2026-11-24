#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Build.ps1 requires Windows x64 and PowerShell 7.'
}
Import-Module (Join-Path $PSScriptRoot 'XrayDistribution.psm1') -Force
$xrayVersion = (Get-XrayDistributionMetadata).Version

function Invoke-DotNet {
    param([string[]] $Arguments)
    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $root
try {
    $sdk = (Get-Content -LiteralPath 'global.json' -Raw | ConvertFrom-Json).sdk.version
    $actualSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actualSdk -cne $sdk) {
        throw "Expected SDK $sdk, got $actualSdk. Install the pinned SDK before building."
    }
    Invoke-DotNet -Arguments @('--info')

    $project = 'src/XrayDesktop/XrayDesktop.csproj'
    $testProject = 'tests/XrayDesktop.Checks/XrayDesktop.Checks.csproj'
    $version = ([xml] (Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.Version
    if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw 'Directory.Build.props must declare a version such as 0.2.1 or 1.0.0-rc.1.'
    }
    if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -cne "v$version") {
        throw "Tag '$($env:GITHUB_REF_NAME)' must match the application version v$version."
    }
    $output = Join-Path $root 'artifacts/phase5'
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    [void] (New-Item -ItemType Directory -Path $output -Force)
    $publish = Join-Path $output 'publish'
    $dist = Join-Path $output 'dist'

    # CI supplies the distribution verified by Get-TestCore.ps1; local builds
    # download it once here. Packaging reuses the same verified bytes.
    $coreExecutable = $env:XRAY_TEST_CORE
    if ([string]::IsNullOrWhiteSpace($coreExecutable)) {
        $coreExecutable = & (Join-Path $PSScriptRoot 'Get-TestCore.ps1') -OutputDirectory (Join-Path $output 'xray-distribution')
    }
    $coreExecutable = (Resolve-Path -LiteralPath $coreExecutable).Path
    $coreDirectory = Split-Path -Parent $coreExecutable
    # Run integration checks against a disposable copy so the distribution used
    # for packaging is never modified.
    $integrationCore = Join-Path $output 'integration-core'
    [void] (New-Item -ItemType Directory -Path $integrationCore -Force)
    foreach ($name in @('xray.exe', 'geoip.dat', 'geosite.dat')) {
        $source = Join-Path $coreDirectory $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Xray distribution is missing $name in $coreDirectory."
        }
        Copy-Item -LiteralPath $source -Destination (Join-Path $integrationCore $name)
    }
    $env:XRAY_TEST_CORE = Join-Path $integrationCore 'xray.exe'

    Invoke-DotNet -Arguments @('restore', 'XrayDesktop.sln')
    Invoke-DotNet -Arguments @('build', 'XrayDesktop.sln', '-c', 'Release', '--no-restore')
    Invoke-DotNet -Arguments @('run', '--project', $testProject, '-c', 'Release', '--no-build', '--no-restore')

    # Publish has its own restore because its RID and bundling properties differ from the solution build.
    $publishProperties = @(
        '-r', 'win-x64',
        '-p:SelfContained=true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )
    Invoke-DotNet -Arguments (@('restore', $project, '-p:Configuration=Release') + $publishProperties)
    Invoke-DotNet -Arguments (@('publish', $project, '-c', 'Release', '--no-restore', '-o', $publish) + $publishProperties)

    $checksExecutable = Join-Path $root 'tests/XrayDesktop.Checks/bin/Release/net10.0-windows/XrayDesktop.Checks.exe'
    & (Join-Path $PSScriptRoot 'New-PortablePackage.ps1') -PublishDirectory $publish -OutputDirectory $dist -ExpectedVersion $version -CoreDirectory $coreDirectory -ChecksExecutable $checksExecutable
    if ($env:GITHUB_STEP_SUMMARY) {
        @(
            '## Windows build and package',
            '',
            ('- SDK: ' + $sdk),
            ('- App version: ' + $version),
            ('- Bundled Xray: v' + $xrayVersion + ' with GeoIP and GeoSite data.'),
            '- Release solution build and console checks passed.',
            '- Portable ZIP layout, internal SHA-256 manifest, pinned core bytes and extracted executable checks passed.',
            '- Extracted package passed version and real-core integration checks using its own bundled xray.exe.',
            '- Proxy transaction checks use a simulated backend; no Windows proxy changes are made by default.',
            '- Diagnostic fixtures exercise loopback proxy rejection, cancellation, TLS rejection and safe export; no public endpoint is required.',
            '- Desktop preference, monitor placement and bounded storage checks passed.',
            ('- Real Xray integration: passed using the bundled core copy.'),
            ('- Read-only WinINet: ' + $(if ($env:XRAY_TEST_WININET_READ -eq '1') { 'passed' } else { 'skipped' })),
            ('- Hidden WinForms construction: ' + $(if ($env:XRAY_TEST_UI -eq '1') { 'passed' } else { 'skipped' })),
            '- Native proxy mutation, interactive GUI, clean-machine and private VLESS acceptance remain separate gates. CI does not establish stable release approval.'
        ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
} finally {
    Pop-Location
}
