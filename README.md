# Xray Desktop

Portable Windows x64 controller for an external Xray-core and raw Xray JSON configurations. Development follows [SDD.md](SDD.md).

**Status:** Phase 5 release-candidate source (`1.0.0-rc.1`), not a stable release. Windows compilation and runtime gates are not yet evidenced. See [Phase 5 acceptance](docs/phase-5.md), [manual acceptance checklist](docs/acceptance-checklist.md), [Phase 4 acceptance](docs/phase-4.md), [Phase 3 acceptance](docs/phase-3.md), [Phase 2 acceptance](docs/phase-2.md) and the historical [Phase 1 record](docs/phase-1.md).

## Users

After a successful Windows build, extract the portable ZIP to a private writable directory, place trusted `xray.exe` (target v26.11.9) and any required assets alongside `XrayDesktop.exe`, then open the GUI. No installed .NET SDK/runtime or elevation is intended. Windows 11 x64 is the primary target; Windows 10 compatibility is unverified. The executable is unsigned.

- Import raw JSON/JSONC as an external reference or a managed copy; create, edit, explicitly save, Save As, reload or remove references. Removing a reference never deletes its file.
- Validate with the native Xray command, then Start, Stop or Restart. Start validates exact snapshot bytes and runs the same snapshot. Select a detected local HTTP/SOCKS endpoint or manual loopback endpoint for listener ownership verification.
- Check core version, state, PID, listener status, raw bounded logs. Other core versions require confirmation before starting. Raw logs and local profile files may contain secrets.
- Enable or restore the current-user default LAN proxy independently of Xray. Select a live, owned HTTP listener, then approve the one-shot HTTPS test to the destination configured in Diagnostics (default `https://www.gstatic.com/generate_204`). SOCKS-only or known authenticated listeners are not eligible. No public request is initiated by merely opening the GUI or starting the core with the default checkbox off.
- Existing settings are recorded before a write, verified on read-back, and restored before Stop/Restart/Close or after a detected child exit. An interrupted transaction is recovered at next launch; use Review / resolve for conflicting external changes. No machine-wide WinHTTP or TUN control.
- Open **Diagnostics** to inspect local state or explicitly run remote DNS/TCP, HTTP proxy, SOCKS5 proxy, direct HTTPS and public IP checks. Direct and IP tests are opt-in; the report preview excludes raw details and addresses. Save destinations and timeout locally if desired. See [Phase 3 acceptance](docs/phase-3.md) for interpretation and limits.
- The notification-area icon is present while the GUI runs. Its menu reopens the window and offers the same gated Start/Stop/Enable/Restore commands. **Settings > Save desktop preferences** can opt into minimize-to-tray; Close and tray Exit still restore the proxy and stop the owned core. Tray commands reopen the window so confirmations stay visible.
- Settings can remember window position/size and log auto-scroll, reset desktop/probe preferences, or clear profile references without deleting the actual configuration files. Raw logs can be saved locally only after a sensitive-data warning. No automatic disk logging.
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

CI now requires the real-core integration and read-only WinINet checks. It downloads the official **v26.11.9 Windows x64** asset into runner temp and verifies a pinned SHA-256 digest before extraction. The core is never packaged. For an equivalent local build:

```powershell
$env:XRAY_TEST_CORE = ./scripts/Get-TestCore.ps1 -OutputDirectory (Join-Path $env:TEMP ('xray-test-' + [guid]::NewGuid().ToString('N')))
$env:XRAY_REQUIRE_TEST_CORE = '1'
$env:XRAY_TEST_WININET_READ = '1'
pwsh -NoProfile -File ./scripts/Build.ps1
```

The integration test uses a temporary freedom-outbound configuration with separate HTTP and SOCKS loopback listeners, an in-process local HTTP origin, malformed-config rejection, stop, unexpected owned-core exit and restart. It makes no public probe requests and never changes Windows proxy settings. Local builds without the required-test flag may skip the real core; a skip is not acceptance evidence. The synthetic child and TCP owner tests run regardless. Phase 2 adds simulated proxy-transaction tests, a real Windows DPAPI round trip in a temporary directory, a native layout check, and a local synthetic CONNECT rejection test. They do not deliberately mutate the Windows proxy. `XRAY_TEST_WININET_READ=1` (required in CI) runs a read-only query without printing values; it is separate from the native write/restore gate in a disposable interactive Windows account. Phase 3 adds loopback-only HTTP/SOCKS5 rejection, unreachable-proxy, cancellation, TLS certificate rejection and structured-report checks; no real public endpoints or proxy mutation are used. Phase 4 adds 13 mandatory placement, preference and bounded-storage checks. An optional `XRAY_TEST_UI=1` check constructs and disposes a hidden WinForms window without firing Shown/Closing; it never starts Xray or writes Windows proxy settings. Enable it via the manually dispatched workflow input `hidden_ui_check` or the local environment. It does not verify actual tray interactions, accessibility or DPI rendering. Phase 5 adds a timeout/cancellation regression check. The candidate is intentionally untagged and cannot be considered stable until all applicable acceptance checks are evidenced. Outputs:

```text
artifacts/phase5/dist/
  XrayDesktop-1.0.0-rc.1-win-x64.zip
  XrayDesktop-1.0.0-rc.1-win-x64.zip.sha256
```

## Repository

- `src/XrayDesktop/`: WinForms, profile storage, core/session management, Windows job, TCP-owner, WinINet and DPAPI APIs, bounded network diagnostics and structured reports.
- `tests/XrayDesktop.Checks/`: dependency-free console checks.
- `scripts/`: build, packaging and published-EXE tests.
- `examples/`: synthetic configuration only.
- `docs/`: quick start and acceptance records.
- `SDD.md`: original design baseline; implementation differences are recorded in phase notes.

Never commit private configs, credentials, binaries, logs or user data. No license has been selected. No stable release has been tagged. Keep commits focused, and do not bypass CI failures.
