#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrayDistribution.psm1') -Force
$distribution = Get-XrayDistribution -OutputDirectory $OutputDirectory
Write-Output (Join-Path $distribution.Directory 'xray.exe')
