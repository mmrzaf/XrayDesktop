#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable,
    [Parameter(Mandatory)]
    [string] $ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path

function Invoke-HeadlessCheck {
    param(
        [string[]] $Arguments,
        [int] $ExpectedExitCode,
        [string] $ExpectedOutput,
        [switch] $ExpectError
    )

    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    # Do not rely on the launch directory being the application directory.
    $start.WorkingDirectory = [System.IO.Path]::GetTempPath()
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) {
            throw 'Could not launch the published application.'
        }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            [void] $process.WaitForExit(5000)
            throw "Published executable timed out: $($Arguments -join ' ')"
        }
        $out = $stdout.GetAwaiter().GetResult().Trim()
        $err = $stderr.GetAwaiter().GetResult().Trim()
        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "Unexpected exit code $($process.ExitCode) for $($Arguments -join ' '): $out $err"
        }
        if ($ExpectError) {
            if ($out -ne '' -or $err -cne $ExpectedOutput) {
                throw "Unexpected error output: stdout='$out'; stderr='$err'"
            }
        } elseif ($out -cne $ExpectedOutput -or $err -ne '') {
            throw "Unexpected output: stdout='$out'; stderr='$err'"
        }
        Write-Host "PASS: published executable $($Arguments -join ' ')"
    } finally {
        $process.Dispose()
    }
}

$usage = 'Usage: XrayDesktop.exe [--version | --self-test | --help]'
Invoke-HeadlessCheck -Arguments @('--version') -ExpectedExitCode 0 -ExpectedOutput $ExpectedVersion
Invoke-HeadlessCheck -Arguments @('--self-test') -ExpectedExitCode 0 `
    -ExpectedOutput "PASS: Xray Desktop $ExpectedVersion; Windows x64; target Xray 26.11.9."
Invoke-HeadlessCheck -Arguments @('--help') -ExpectedExitCode 0 -ExpectedOutput $usage
Invoke-HeadlessCheck -Arguments @('--unknown') -ExpectedExitCode 2 -ExpectedOutput $usage -ExpectError
Invoke-HeadlessCheck -Arguments @('--version', '--self-test') -ExpectedExitCode 2 -ExpectedOutput $usage -ExpectError
