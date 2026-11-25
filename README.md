# Xray Desktop

Portable Windows x64 controller with a bundled, verified Xray-core and raw Xray JSON configurations. Development follows [SDD.md](SDD.md).

**Status:** `0.4.0`, pending Windows acceptance. See the [manual acceptance checklist](docs/acceptance-checklist.md) and the [CI and release workflow](docs/ci-release.md). Older phase records in `docs/` are historical.

## Users

Download the portable ZIP, extract the whole archive to a folder you can write to, and run `XrayDesktop.exe`. The archive already contains the official Xray-core v26.11.9 (`xray.exe`, `geoip.dat`, `geosite.dat`); no separate download, installed .NET runtime or administrator rights are needed. Windows 11 x64 is the primary target; Windows 10 is unverified. The executable is unsigned, so SmartScreen may warn.

1. **Add a profile.** Copy a `vless://`, `vmess://`, `trojan://` or `ss://` link (several, one per line, also work) and choose **Add > Paste link from clipboard**. Or **Add > Import file…** / drag a `.json`/`.jsonc` Xray configuration onto the window. The bundled core checks every profile before it is saved.
2. **Click Connect.** The app starts Xray, makes one HTTPS request through it to prove the server works, and only then points Windows at it. If the server does not answer, Windows is left untouched and the window says so in plain words.
3. **Click Disconnect** (or exit). Your previous Windows proxy settings are put back exactly as they were, before Xray stops.

What it does for you:

- **A listener is always available.** Windows needs an HTTP proxy port. If a profile only has SOCKS, or its ports are taken, the app adds or moves a local listener in the copy it runs. Your profile file is never changed.
- **Windows is not left broken.** Your settings are recorded before the first change and restored when you disconnect, exit, log off, shut down or if Xray stops. If the app itself is killed or crashes, a small guard process restores them within seconds; if even that fails, the next launch restores them. If someone else changed the settings meanwhile, the app does not overwrite them and asks you.
- **It notices a dead server.** While connected, one small request every 20 seconds checks the tunnel. Two misses turn the status to *Connection problem* and raise a notification.
- **Closing the window while connected keeps it running** in the notification area (green icon). Use the tray menu to disconnect or exit. Opening the exe again just shows the running window.
- **More** menu: edit a profile's JSON (validated by the core), rename, remove, review Windows proxy state. **Logs** and **Diagnostics** tabs hold the raw output and optional network checks for troubleshooting; raw logs may contain secrets and are only saved when you ask.

Replacing `xray.exe` or the geodata with another compatible build is an optional advanced step; the app checks the version and warns when it differs from the tested baseline. The [third-party notices](docs/THIRD-PARTY-NOTICES.txt) record the bundled core and geodata. Use only profiles from sources you trust: the GUI does not sandbox Xray. There is no telemetry and no core download.

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

The integration test uses a temporary freedom-outbound configuration with separate HTTP and SOCKS loopback listeners, an in-process local HTTP origin, malformed-config rejection, stop, unexpected owned-core exit and restart. It makes no public probe requests and never changes Windows proxy settings. The checks harness may skip the real core when `XRAY_TEST_CORE` is unset, but `Build.ps1` always supplies it and packaging requires it; a skip is not acceptance evidence. The synthetic child and TCP owner tests run regardless. Phase 2 adds simulated proxy-transaction tests, a real Windows DPAPI round trip in a temporary directory, a native layout check, and a local synthetic CONNECT rejection test. They do not deliberately mutate the Windows proxy. `XRAY_TEST_WININET_READ=1` (required in CI) runs a read-only query without printing values; it is separate from the native write/restore gate in a disposable interactive Windows account. Phase 3 adds loopback-only HTTP/SOCKS5 rejection, unreachable-proxy, cancellation, TLS certificate rejection and structured-report checks; no real public endpoints or proxy mutation are used. Phase 4 adds 13 mandatory placement, preference and bounded-storage checks. An optional `XRAY_TEST_UI=1` check constructs and disposes a hidden WinForms window without firing Shown/Closing; it never starts Xray or writes Windows proxy settings. Enable it via the manually dispatched workflow input `hidden_ui_check` or the local environment. It does not verify actual tray interactions, accessibility or DPI rendering. Phase 5 adds a timeout/cancellation regression check. Release 0.4.0 adds 15 checks for share-link import, run-copy listener planning, guard arguments and managed-profile deletion; the connect flow, guard, shutdown and tray behavior are covered only by the manual acceptance checklist. The candidate may be tagged after the fixed CI passes, but cannot be considered stable until all applicable acceptance checks are evidenced. Outputs:

```text
artifacts/phase5/dist/
  XrayDesktop-0.4.0-win-x64.zip
  XrayDesktop-0.4.0-win-x64.zip.sha256
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
