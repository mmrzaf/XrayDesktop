# Xray Desktop

Portable Windows x64 controller for an external Xray-core and raw Xray JSON configurations. Development follows [SDD.md](SDD.md).

**Status:** Phase 3 source preview (`0.1.0-preview.3`). Phase 0–3 Windows build/runtime gates are not yet evidenced. See [Phase 3 acceptance](docs/phase-3.md), [Phase 2 acceptance](docs/phase-2.md) and the historical [Phase 1 record](docs/phase-1.md).

## Users

After a successful Windows build, extract the portable ZIP to a private writable directory, place trusted `xray.exe` (target v26.11.9) and any required assets alongside `XrayDesktop.exe`, then open the GUI. No installed .NET SDK/runtime or elevation is intended. Windows 11 x64 is the primary target; Windows 10 compatibility is unverified. The executable is unsigned.

- Import raw JSON/JSONC as an external reference or a managed copy; create, edit, explicitly save, Save As, reload or remove references. Removing a reference never deletes its file.
- Validate with the native Xray command, then Start, Stop or Restart. Start validates exact snapshot bytes and runs the same snapshot. Select a detected local HTTP/SOCKS endpoint or manual loopback endpoint for listener ownership verification.
- Check core version, state, PID, listener status, raw bounded logs. Other core versions require confirmation before starting. Raw logs and local profile files may contain secrets.
- Enable or restore the current-user default LAN proxy independently of Xray. Select a live, owned HTTP listener, then approve the one-shot HTTPS test to the destination configured in Diagnostics (default `https://www.gstatic.com/generate_204`). SOCKS-only or known authenticated listeners are not eligible. No public request is initiated by merely opening the GUI or starting the core with the default checkbox off.
- Existing settings are recorded before a write, verified on read-back, and restored before Stop/Restart/Close or after a detected child exit. An interrupted transaction is recovered at next launch; use Review / resolve for conflicting external changes. No machine-wide WinHTTP or TUN control. Tray support is a later phase.
- Open **Diagnostics** to inspect local state or explicitly run remote DNS/TCP, HTTP proxy, SOCKS5 proxy, direct HTTPS and public IP checks. Direct and IP tests are opt-in; the report preview excludes raw details and addresses. Save destinations and timeout locally if desired. See [Phase 3 acceptance](docs/phase-3.md) for interpretation and limits.
- If force-killed, the Windows proxy may remain set until you reopen the app or manually restore your old settings. See [recovery instructions](#proxy-recovery). No telemetry or core downloads.

The bundled example has a `freedom` outbound and is not a VPN or privacy-protecting connection. Use only trusted configurations; the GUI does not sandbox Xray. See [portable instructions](docs/portable-readme.txt).

## Proxy recovery

Recovery is stored per Windows user, outside the portable folder, at `%LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi`. This allows recovery after moving the application. The journal is protected with current-user Windows DPAPI and is never sent to GitHub or included in default logs. Do not delete it merely because Xray has stopped.

If the app was killed, reopen it under the same Windows user. Recovery occurs before checking `xray.exe`. If current settings differ from both the recorded original and intended state, the app does not guess: use **Review / resolve** to compare values and explicitly restore the original or retain external settings. Treat those values as sensitive. If it cannot restore, it offers to keep the core running or force stop with the journal retained.

If Windows remains stuck or the journal is unreadable, go to **Settings > Network & Internet > Proxy** and restore your previously known manual proxy, setup script and automatically detect settings. Do not indiscriminately disable a corporate or other pre-existing proxy. If you cannot recover the original settings, ask the network administrator. Only after manually verifying settings and closing every Xray Desktop copy, an unreadable journal may be moved to a private backup location to unblock future use; never treat that as an automatic restore. DPAPI-protected bytes are user-bound, not portable to another account. Use a private test account for the first native proxy-write tests.

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

This optional test uses a temporary direct-only configuration with a dynamically allocated loopback port; it makes no external network requests and never changes Windows proxy settings. Absence of the core is explicitly reported as skipped. A skipped test is not acceptance evidence. The synthetic child and TCP owner tests run regardless. Phase 2 adds simulated proxy-transaction tests, a real Windows DPAPI round trip in a temporary directory, a native layout check, and a local synthetic CONNECT rejection test. They do not deliberately mutate the Windows proxy. Optional `XRAY_TEST_WININET_READ=1` runs a read-only query without printing values; it is separate from the native write/restore gate in a disposable interactive Windows account. Phase 3 adds loopback-only HTTP/SOCKS5 rejection, unreachable-proxy, cancellation, TLS certificate rejection and structured-report checks; no real public endpoints or proxy mutation are used. Outputs:

```text
artifacts/phase3/dist/
  XrayDesktop-0.1.0-preview.3-win-x64.zip
  XrayDesktop-0.1.0-preview.3-win-x64.zip.sha256
```

## Repository

- `src/XrayDesktop/`: WinForms, profile storage, core/session management, Windows job, TCP-owner, WinINet and DPAPI APIs, bounded network diagnostics and structured reports.
- `tests/XrayDesktop.Checks/`: dependency-free console checks.
- `scripts/`: build, packaging and published-EXE tests.
- `examples/`: synthetic configuration only.
- `docs/`: quick start and acceptance records.
- `SDD.md`: original design baseline; implementation differences are recorded in phase notes.

Never commit private configs, credentials, binaries, logs or user data. No license has been selected. Keep commits focused, and do not bypass CI failures.
