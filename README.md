# Xray Desktop

Portable Windows x64 controller for an external Xray-core and raw Xray JSON configurations. Development follows [SDD.md](SDD.md).

**Status:** Phase 1 source preview (`0.1.0-preview.1`). Phase 0 and Phase 1 Windows build/runtime gates are not yet evidenced. No system proxy integration yet. See [Phase 1 acceptance](docs/phase-1.md).

## Users

After a successful Windows build, extract the portable ZIP to a private writable directory, place trusted `xray.exe` (target v26.11.9) and any required assets alongside `XrayDesktop.exe`, then open the GUI. No installed .NET SDK/runtime or elevation is intended. Windows 11 x64 is the primary target; Windows 10 compatibility is unverified. The executable is unsigned.

- Import raw JSON/JSONC as an external reference or a managed copy; create, edit, explicitly save, Save As, reload or remove references. Removing a reference never deletes its file.
- Validate with the native Xray command, then Start, Stop or Restart. Start validates exact snapshot bytes and runs the same snapshot. Select a detected local HTTP/SOCKS endpoint or manual loopback endpoint for listener ownership verification.
- Check core version, state, PID, listener status, raw bounded logs. Other core versions require confirmation before starting. Raw logs and local profile files may contain secrets.
- Windows system proxy management, internet diagnostics and tray support are later phases. No automatic network probes, core downloads or telemetry.

The bundled example has a `freedom` outbound and is not a VPN or privacy-protecting connection. Use only trusted configurations; the GUI does not sandbox Xray. See [portable instructions](docs/portable-readme.txt).

## Developers and CI

Windows x64, PowerShell 7 and pinned .NET SDK 10.0.404 (`global.json`). No third-party package references. Framework/runtime packs are restored as needed. The GitHub Actions workflow uses `windows-2025`, SHA-pinned actions and read-only permissions.

```powershell
pwsh -NoProfile -File ./scripts/Build.ps1
```

The pipeline builds Release with warnings as errors, executes the console checks, publishes the single-file self-contained executable, verifies ZIP entries and SHA-256 checksums, runs the extracted EXE headless in a Unicode path from a different working directory, and uploads only on success.

For an additional real-core integration check, obtain the trusted **v26.11.9 Windows x64** core yourself and set the absolute path before running the same build:

```powershell
$env:XRAY_TEST_CORE = 'C:\trusted-xray\xray.exe'
pwsh -NoProfile -File ./scripts/Build.ps1
```

This optional test uses a temporary direct-only configuration with a dynamically allocated loopback port; it makes no external network requests and never changes Windows proxy settings. Absence of the core is explicitly reported as skipped. A skipped test is not acceptance evidence. The synthetic child and TCP owner tests run regardless. Outputs:

```text
artifacts/phase1/dist/
  XrayDesktop-0.1.0-preview.1-win-x64.zip
  XrayDesktop-0.1.0-preview.1-win-x64.zip.sha256
```

## Repository

- `src/XrayDesktop/`: WinForms, profile storage, core/session management, Windows job and TCP-owner APIs.
- `tests/XrayDesktop.Checks/`: dependency-free console checks.
- `scripts/`: build, packaging and published-EXE tests.
- `examples/`: synthetic configuration only.
- `docs/`: quick start and acceptance records.
- `SDD.md`: original design baseline; implementation differences are recorded in phase notes.

Never commit private configs, credentials, binaries, logs or user data. No license has been selected. Keep commits focused, and do not bypass CI failures.
