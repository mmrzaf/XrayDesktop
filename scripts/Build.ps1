#Requires -Version 7.0
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'Build.ps1 requires Windows x64 and PowerShell 7.'
}

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
    $version = ([xml] (Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
    $output = Join-Path $root 'artifacts/phase0'
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    [void] (New-Item -ItemType Directory -Path $output -Force)
    $publish = Join-Path $output 'publish'
    $dist = Join-Path $output 'dist'

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

    & (Join-Path $PSScriptRoot 'New-PortablePackage.ps1') -PublishDirectory $publish -OutputDirectory $dist -ExpectedVersion $version
    if ($env:GITHUB_STEP_SUMMARY) {
        @(
            '## Phase 0 Windows build',
            '',
            ('- SDK: ' + $sdk),
            ('- App version: ' + $version),
            '- Release solution build and console checks passed.',
            '- Portable ZIP layout, internal SHA-256 manifest and extracted executable checks passed.',
            '- Xray integration, visual GUI testing and clean-machine acceptance remain separate gates.'
        ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
} finally {
    Pop-Location
}
