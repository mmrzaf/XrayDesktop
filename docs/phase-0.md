# Phase 0 acceptance

Baseline: .NET SDK 10.0.404, WinForms `net10.0-windows`, Xray-core v26.11.9 (target only), Windows x64. The app version is `0.1.0-preview.0`. The SDK release and `windows-2025` runner label were checked against official documentation on 2026-11-24.

## Automated gate

The Windows build must complete every step below. The workflow only uploads the portable package after all steps succeed.

| Check | Implementation | Initial status |
|---|---|---|
| Restore and Release compile | `scripts/Build.ps1` | Pending Windows CI |
| Nine console assertions | `tests/XrayDesktop.Checks` | Pending Windows CI |
| Self-contained single-file publish | `scripts/Build.ps1` | Pending Windows CI |
| Exact package contents and SHA-256 verification | `scripts/New-PortablePackage.ps1` | Pending Windows CI |
| Extracted EXE `--version`, `--self-test`, `--help`, invalid and combined arguments | `scripts/Test-PublishedApp.ps1` | Pending Windows CI |
| Artifact upload | `.github/workflows/build.yml` | Pending Windows CI |

The ZIP writer uses a fixed entry order and timestamp, but the whole build is not certified byte-for-byte reproducible across changing runner images. The pinned SDK, explicit restore, controlled package inputs and checksums establish a repeatable build procedure. Lock files have not been generated because there are no package references and the SDK has not been executed here; revisit after the first real restore or if package dependencies are added.

## Manual GUI smoke gate

On a supported Windows 11 x64 test machine (preferably a clean VM without a separate .NET runtime or SDK):

1. Download the successful CI artifact and extract its portable ZIP into a writable path with spaces and non-ASCII characters.
2. Verify the archive SHA-256 against the supplied `.zip.sha256` and verify the internal manifest.
3. Confirm there is no `xray.exe` next to the GUI. Record the current Windows proxy setting without changing it.
4. Double-click `XrayDesktop.exe` as a standard user. Confirm it opens without a UAC elevation request or missing .NET prompt (SmartScreen may appear because the EXE is unsigned).
5. Confirm the visible version and target core, and that the UI clearly says this is a Phase 0 preview.
6. Resize the window, check labels at 100%, 150% and 200% scaling, then close it using Close, Escape and the window close button on separate launches.
7. Confirm the process exits, no Xray process is launched, no user settings or profiles are created, and the Windows proxy is unchanged.
8. Record the OS build, artifact SHA-256, commit SHA, result and any observed errors. Do not mark the gate passed without actual evidence.

Real Xray invocation, proxy transactions, private configurations and diagnostics belong to later phases. No automatic release or stable version is published by Phase 0 CI.

## Initial verification limitations

The implementation was prepared in a Linux environment without the .NET SDK, PowerShell or Windows runtime. Network access for installing the SDK was unavailable. The Git history and source files can be inspected locally, but the C# build, PowerShell pipeline, executable and actual GUI have not been run. No connected repository for this project was available. The first Windows workflow result is therefore still required; do not treat the existence of this document or the package workflow as a passing build.

## Reference inputs

- SDK: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Runner: https://github.com/actions/runner-images/blob/main/README.md
- Checkout v5.0.0: https://github.com/actions/checkout/commit/08c6903cd8c0fde910a37f88322edcfb5dd907a8
- Setup .NET v5.0.1: https://github.com/actions/setup-dotnet/commit/2016bd2012dba4e32de620c46fe006a3ac9f0602
- Upload artifact v4.6.2: https://github.com/actions/upload-artifact/commit/ea165f8d65b6e75b540449e92b4886f43607fa02
