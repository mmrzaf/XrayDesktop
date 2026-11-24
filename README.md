# Xray Desktop

A portable Windows x64 controller for an external Xray-core executable and raw Xray JSON configurations. The application is being developed in small, gated phases according to [SDD.md](SDD.md).

**Current status:** Phase 0 development preview (`0.1.0-preview.0`). The source foundation is present; Windows CI and visual acceptance have not yet been run. This is not a functional proxy client.

## Requirements

For users (after a successful Windows build): a supported Windows 11 x64 machine. No installed .NET runtime, SDK, development tools or administrator rights are required. Windows 10 support is pending separate compatibility testing. The Xray v26.11.9 core is a future integration requirement, not a Phase 0 dependency.

For developers or CI: Windows x64, PowerShell 7, and the exact [.NET SDK](https://dotnet.microsoft.com/download/dotnet/10.0) specified in `global.json` (10.0.404). There are no third-party NuGet package references. Framework and runtime packs are restored from nuget.org as required.

## Build and package

GitHub Actions runs the [Windows build](.github/workflows/build.yml) workflow on pushes to `main`, pull requests and manual dispatch. It uses the `windows-2025` runner and installs the SDK from `global.json`. Action references are pinned to full commit SHAs; CI permissions are read-only. No live Xray server, credentials or secrets are needed.

Developers can execute the same pipeline from the repository root:

```powershell
pwsh -NoProfile -File ./scripts/Build.ps1
```

The script replaces only its generated `artifacts/phase0/` directory, restores and builds the solution in Release, runs the console checks, restores for single-file Windows x64 publishing, publishes and creates a portable ZIP. It checks the archive's exact contents and checksums, then launches the extracted executable with five headless argument combinations. A failed step stops the build and prevents artifact upload.

Successful output (also available as a GitHub Actions artifact):

```text
artifacts/phase0/dist/
  XrayDesktop-0.1.0-preview.0-win-x64.zip
  XrayDesktop-0.1.0-preview.0-win-x64.zip.sha256
```

The ZIP contains the standalone GUI, a quick-start README, a credential-free direct-only Xray example and an internal checksum manifest. It does not contain Xray itself. Extract into a writable directory and launch `XrayDesktop.exe`. The unsigned preview may trigger SmartScreen. The sample configuration does not provide a VLESS connection or privacy protection.

## Repository layout

```text
src/XrayDesktop/          WinForms bootstrap and application metadata
tests/XrayDesktop.Checks/ No-framework console checks
scripts/                  Windows build, packaging and executable smoke checks
examples/                 Synthetic configuration, no credentials
docs/                     Portable README and phase acceptance notes
.github/workflows/        Windows CI
SDD.md                    Approved design baseline
```

## Phase 0 behavior

- No arguments: show the resizable, DPI-aware application shell.
- `--version`, `--self-test`, `--help`: run headless and exit with status 0 on success.
- Unknown or combined arguments: exit with status 2 and print usage to stderr.
- Self-test failure: exit with status 1. A passing self-test is not evidence of Xray connectivity or system proxy behavior.

Phase 0 does not start Xray, read profiles, write settings, make network requests or change the system proxy. Controls for those features are intentionally absent until implementation. See [Phase 0 acceptance](docs/phase-0.md) for current evidence and the remaining gate.

Do not commit real Xray profiles, keys, tokens, binaries, logs or user data. The sample under `examples/` is synthetic. Keep commits focused; use the same build script locally and in CI; resolve errors rather than bypass checks. No license has been selected yet, and the repository is not intended for public distribution until the owner decides on licensing.
