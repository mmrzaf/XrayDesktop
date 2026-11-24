# Xray Desktop

Portable Windows x64 controller with a bundled, verified Xray-core and raw Xray JSON configurations. Development follows [SDD.md](SDD.md).

**Status:** Phase 5 source preview (`0.3.0`), pending Windows acceptance. The first Windows run compiled with zero warnings and errors, but failed two runtime checks; targeted fixes await rerun. See [CI and release workflow](docs/ci-release.md). See [Phase 5 acceptance](docs/phase-5.md), [manual acceptance checklist](docs/acceptance-checklist.md), [Phase 4 acceptance](docs/phase-4.md), [Phase 3 acceptance](docs/phase-3.md), [Phase 2 acceptance](docs/phase-2.md) and the historical [Phase 1 record](docs/phase-1.md).

## Users

Download the portable ZIP, extract the whole archive to a private writable directory, and launch `XrayDesktop.exe`. The archive already contains the official Xray-core v26.11.9 `xray.exe`, `geoip.dat` and `geosite.dat`; no separate Xray download, installed .NET runtime, administrator elevation or development tools are required. Import an Xray JSON configuration, validate it with the bundled core, start it, then optionally enable the system proxy. The **Start** tab is numbered and a status header on every tab shows the current state, the next useful action and the last error. Windows 11 x64 is the primary target; Windows 10 compatibility is unverified. The executable is unsigned.

Replacing `xray.exe` or the geodata files with another compatible build is an optional advanced operation; the application still detects the adjacent executable, checks its version and warns when it is outside the tested baseline. The [third-party notices](docs/THIRD-PARTY-NOTICES.txt) record the bundled core and geodata provenance and licenses.

- Import raw JSON/JSONC as an external reference or a managed copy; create, edit, explicitly save, Save As, reload or remove references. You can also drag a `.json`/`.jsonc` file onto the window. Removing a reference never deletes its file.
- The Start tab walks through configuration, optional listener verification, validate/start and optional Windows proxy. It shows the detected listeners, outbound tags and routing-rule count, warns about a port already in use, and offers **Use free port…** to create a validated managed copy with the selected listener moved. A profile with only a SOCKS inbound can run, but Windows system proxy requires an anonymous HTTP listener; **Add HTTP listener…** creates a validated managed copy without changing the original.
- Validate with the native Xray command, then Start, Stop or Restart. Start validates exact snapshot bytes and runs the same snapshot. Validation output is shown inline, and **Run local checks** combines the core check, native validation and non-invasive Local diagnostics. Select a detected local HTTP/SOCKS endpoint or manual loopback endpoint for listener ownership verification.
- Check core version, state, PID, listener status, raw bounded logs. Other core versions require confirmation before starting. Raw logs and local profile files may contain secrets; **Find…** searches the bounded log buffer.
- Enable or restore the current-user default LAN proxy independently of Xray. Select a live, owned HTTP listener, then approve the one-shot HTTPS test to the destination configured in Diagnostics (default `https://www.gstatic.com/generate_204`). SOCKS-only or known authenticated listeners are not eligible. No public request is initiated by merely opening the GUI or starting the core with the default checkbox off.
- Existing settings are recorded before a write, verified on read-back, and restored before Stop/Restart/Close or after a detected child exit. An interrupted transaction is recovered at next launch; use Review / resolve for conflicting external changes. No machine-wide WinHTTP or TUN control.
- Open **Diagnostics** to inspect local state or explicitly run remote DNS/TCP, HTTP proxy, SOCKS5 proxy, direct HTTPS and public IP checks. Direct and IP tests are opt-in; the report preview excludes raw details and addresses. Save destinations and timeout locally if desired. See [Phase 3 acceptance](docs/phase-3.md) for interpretation and limits.
- The notification-area icon is present while the GUI runs. Its menu reopens the window and offers the same gated Start/Stop/Enable/Restore commands plus local checks. Unexpected core exits and failures raise a tray notification when the window is hidden. **Settings > Save desktop preferences** can opt into minimize-to-tray; Close and tray Exit still restore the proxy and stop the owned core. Tray commands reopen the window so confirmations stay visible.
- Settings can remember window position/size and log auto-scroll, reset desktop/probe preferences, or clear profile references without deleting the actual configuration files. Shortcuts are listed in Settings. Raw logs can be saved locally only after a sensitive-data warning. No automatic disk logging.
- If force-killed, the Windows proxy may remain set until you reopen the app or manually restore your old settings. See [recovery instructions](#proxy-recovery). No telemetry or core downloads.

The bundled example has a `freedom` outbound and is not a VPN or privacy-protecting connection. Use only trusted configurations; the GUI does not sandbox Xray. See [portable instructions](docs/portable-readme.txt).

## Proxy recovery

Recovery is stored per Windows user, outside the portable folder, at `%LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi`. This allows recovery after moving the application. The journal is protected with current-user Windows DPAPI and is never sent to GitHub or included in default logs. Do not delete it merely because Xray has stopped.

If the app was killed, reopen it under the same Windows user. Recovery occurs before checking `xray.exe`. If current settings differ from both the recorded original and intended state, the app does not guess: use **Review / resolve** to compare values and explicitly restore the original or retain external settings. Treat those values as sensitive. If it cannot restore, it offers to keep the core running or force stop with the journal retained.

If Windows remains stuck or the journal is unreadable, go to **Settings > Network & Internet > Proxy** and restore your previously known manual proxy, setup script and automatically detect settings. Do not indiscriminately disable a corporate or other pre-existing proxy. If you cannot recover the original settings, ask the network administrator. Only after manually verifying settings and closing every Xray Desktop copy, an unreadable journal may be moved to a private backup location to unblock future use; never treat that as an automatic restore. DPAPI-protected bytes are user-bound, not portable to another account. Use a private test account for the first native proxy-write tests.

## Developers and CI

Windows x64, PowerShell 7 and pinned .NET SDK 10.0.404 (`global.json`). No third-party .NET package references. Framework/runtime packs are restored as needed. The GitHub Actions workflow uses `windows-2025`, SHA-pinned actions and read-only build permissions. Only the tag-triggered publication job has `contents: write`.

```powershell
pwsh -NoProfile -File ./scripts/Build.ps1
```

The pipeline builds Release with warnings as errors, executes the console checks, publishes the single-file self-contained executable, downloads and verifies the pinned Xray distribution once, bundles the core and geodata, verifies ZIP entries, payload hashes and internal checksums, runs the extracted GUI and bundled core in a Unicode path from a different working directory, and uploads only on success.

CI requires the real-core integration and read-only WinINet checks. It downloads the official **v26.11.9 Windows x64** asset into runner temp, verifies a pinned SHA-256 digest and the pinned file hashes before extraction, and reuses those bytes for both integration and packaging. For an equivalent local build, `Build.ps1` downloads and verifies the distribution itself when `XRAY_TEST_CORE` is unset; an existing verified distribution can be supplied explicitly:

```powershell
$env:XRAY_TEST_CORE = ./scripts/Get-TestCore.ps1 -OutputDirectory (Join-Path $env:TEMP ('xray-test-' + [guid]::NewGuid().ToString('N')))
$env:XRAY_REQUIRE_TEST_CORE = '1'
$env:XRAY_TEST_WININET_READ = '1'
pwsh -NoProfile -File ./scripts/Build.ps1
```

The integration test uses a temporary freedom-outbound configuration with separate HTTP and SOCKS loopback listeners, an in-process local HTTP origin, malformed-config rejection, stop, unexpected owned-core exit and restart. It makes no public probe requests and never changes Windows proxy settings. The checks harness may skip the real core when `XRAY_TEST_CORE` is unset, but `Build.ps1` always supplies it and packaging requires it; a skip is not acceptance evidence. The synthetic child and TCP owner tests run regardless. Phase 2 adds simulated proxy-transaction tests, a real Windows DPAPI round trip in a temporary directory, a native layout check, and a local synthetic CONNECT rejection test. They do not deliberately mutate the Windows proxy. `XRAY_TEST_WININET_READ=1` (required in CI) runs a read-only query without printing values; it is separate from the native write/restore gate in a disposable interactive Windows account. Phase 3 adds loopback-only HTTP/SOCKS5 rejection, unreachable-proxy, cancellation, TLS certificate rejection and structured-report checks; no real public endpoints or proxy mutation are used. Phase 4 adds 13 mandatory placement, preference and bounded-storage checks. An optional `XRAY_TEST_UI=1` check constructs and disposes a hidden WinForms window without firing Shown/Closing; it never starts Xray or writes Windows proxy settings. Enable it via the manually dispatched workflow input `hidden_ui_check` or the local environment. It does not verify actual tray interactions, accessibility or DPI rendering. Phase 5 adds a timeout/cancellation regression check. The candidate may be tagged after the fixed CI passes, but cannot be considered stable until all applicable acceptance checks are evidenced. Outputs:

```text
artifacts/phase5/dist/
  XrayDesktop-0.3.0-win-x64.zip
  XrayDesktop-0.3.0-win-x64.zip.sha256
```

`scripts/XrayDistribution.psm1` is the single source for the pinned core version, release asset, archive SHA-256 and packaged file hashes. `scripts/Get-TestCore.ps1` downloads and verifies that distribution once per workflow run; `Build.ps1` reuses it for integration checks and packaging. Packaging rejects missing, corrupted or unexpected files, checks that the packaged core and geodata match the verified distribution byte-for-byte, runs the extracted `xray.exe` version command, exercises the extracted GUI, and runs a real-core integration check from the extracted package. The archive includes the upstream core license, geodata licenses and `THIRD-PARTY-NOTICES.txt`; no Xray binary or geodata is committed to Git.

## Releases

CI runs on pushes to `main` and `develop`, pull requests targeting either branch, manual dispatch, and pushes of `v*` tags. A matching version tag publishes the tested Windows ZIP and SHA-256 file to GitHub Releases only after the required build and release guard checks pass. Branch and PR runs never publish releases. See [CI and release workflow](docs/ci-release.md) for the version and tag procedure.

## Repository

- `src/XrayDesktop/`: WinForms, profile storage, core/session management, Windows job, TCP-owner, WinINet and DPAPI APIs, bounded network diagnostics and structured reports.
- `tests/XrayDesktop.Checks/`: dependency-free console checks.
- `scripts/`: build, packaging and published-EXE tests.
- `examples/`: synthetic configuration only.
- `docs/`: quick start and acceptance records.
- `SDD.md`: original design baseline; implementation differences are recorded in phase notes.

Never commit private configs, credentials, binaries, logs or user data. No license has been selected. No stable release has been tagged. Keep commits focused, and do not bypass CI failures.
