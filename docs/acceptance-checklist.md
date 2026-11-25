# Release acceptance record — template

All rows are **NOT RUN** until actual evidence is entered. Source inspection, a successful compile, and simulated tests do not establish real proxy restoration or browser behavior. Keep this record separate from private profiles, PAC URLs, raw logs and public IPs.

## Evidence header

- Candidate version / Git SHA:
- CI run URL / result:
- Portable ZIP SHA-256:
- Windows edition, build, architecture and user privilege:
- Xray version and binary source (bundled archive SHA-256):
- Test date / tester:
- Disposable VM snapshot or isolated account confirmed:
- Original proxy settings recorded privately (manual, bypass, PAC, autodetect):

## Before any native mutation

Use a disposable Windows VM or isolated account with a known, recoverable network configuration, no work/corporate policy, and access to Windows Settings > Network & Internet > Proxy even when internet is unavailable. Take a VM snapshot if possible. Extract the candidate archive, which already contains Xray v26.11.9 and its geodata, into a private writable directory; keep a second portable copy for recovery. Use the synthetic direct configuration first; it does not protect privacy. Do not use a real corporate proxy for destructive tests. Never kill unrelated Xray instances. For unexpected exit tests, identify the owned PID in the GUI and verify it again in Task Manager before ending it. Do not post raw settings or logs in issues. Stop testing and restore the recorded settings if any unexpected mutation occurs.

| SDD ID | Procedure and required evidence | Result |
|---|---|---|
| A01 | Launch with the bundled xray.exe temporarily renamed or replaced by a non-executable file in the disposable copy. GUI opens, errors visible, proxy unchanged; exercise version timeout in controlled fixture. | NOT RUN |
| A02 | Import malformed JSON and valid syntax rejected by Xray; separately test missing required geodata. Validate/Start fail, original file bytes unchanged, proxy untouched. | NOT RUN |
| A03 | Edit a copy containing unknown fields, Unicode, comments, relative paths and spaces. Cancel preserves bytes; explicit Save/Save As and reload preserve intended fields; relative paths resolve. | NOT RUN |
| A04 | Import a SOCKS-only profile, and one whose ports are occupied by an unrelated local process in the isolated account. Connect must succeed using an added or moved listener in the run copy; the profile file bytes stay unchanged; the unrelated process is never claimed or killed. | NOT RUN |
| A05 | Paste each supported link type (vless REALITY, vless ws+tls, vmess, trojan, ss, several at once) from the clipboard. The profile validates with the bundled core and Connect works against a test server. A bad link shows a plain message and adds nothing. | NOT RUN |
| A06 | Repeat start/stop/restart, cancel during validation/start, close during a command. At most one owned child; responsive GUI and no leaked owned processes. | NOT RUN |
| A07 | With known baseline settings, Connect then Disconnect; repeat three times. Windows proxy shows the app's loopback server while connected and the exact original values (manual, bypass, PAC, autodetect) afterwards. A failing server must leave Windows untouched. | NOT RUN |
| A08 | In isolation, exercise known manual, PAC and auto-detect configurations separately. Capture each original locally, consent, acquire, restore and compare all editable settings. Exercise policy-denied and write/refresh failures in a controlled fixture; fake-only results are not native evidence. | NOT RUN |
| A09 | With active managed proxy, edit Windows settings externally to a known different value; app reports conflict and does not overwrite it automatically. Review and explicitly restore or retain, repeat using second portable copy. Verify partial-write recovery using controlled fault fixture. | NOT RUN |
| A10 | While connected, end only the owned Xray PID. The window shows the stopped message, original settings are restored, and browser connectivity returns to baseline. | NOT RUN |
| A11 | While connected, end only the GUI process (Task Manager, End task, and a hard kill). Xray ends; the guard process restores the original settings within about 5 seconds; with the guard also killed, the next launch restores them. | NOT RUN |
| A12 | Review real loopback integration and no-fallback fixture results; in GUI test valid HTTP/SOCKS, unreachable proxy, TLS error, timeout, cancel and explicit direct consent. | NOT RUN |
| A13 | Run export isolation fixtures, preview report; ensure no UUID, token, address, IP, URL, raw error or private path is exported. Raw log export must prompt. | NOT RUN |
| A14 | On clean non-admin Windows 11 x64 with no separately installed .NET runtime/SDK, extract the release candidate from a Unicode/spaced path, verify the bundled xray.exe/geodata against SHA256SUMS.txt, and run headless and GUI checks. | NOT RUN |
| A15 | Import a private trusted VLESS config locally. Validate, start, test explicit HTTPS and browser through system proxy, then restore and verify original settings. Do not upload credentials. | NOT RUN |
| A16 | Test keyboard navigation, 100/150/200% DPI, resized and multi-monitor windows, tray show/minimize/exit, validation/log flooding and cancellation responsiveness. | NOT RUN |
| A17 | Repeat representative clean-machine and proxy restoration checks on a supported Windows 10 edition before claiming compatibility; otherwise explicitly mark Windows 10 unsupported/unverified. | NOT RUN |
| A18 | While connected, log off and shut down (separately). On the next sign-in, Windows has the original proxy settings and the internet works before the app is started. | NOT RUN |
| A19 | While connected, close the window: connection continues from the tray (green icon) with a one-time notice. Launch the exe again: the running window appears, no second instance. Tray Disconnect and Exit restore the original settings. | NOT RUN |
| A20 | While connected, stop the server (or block it). Within about a minute the status changes to Connection problem with a notification; when the server returns it recovers to Connected. | NOT RUN |

For every result, record PASS, FAIL, or NOT RUN with actual observation and a safe reference to evidence. Record reasons for any non-applicable platform claim. Do not replace a failed check with a skip.

## Emergency recovery and cleanup

Normal order: Restore system proxy, Stop, Close. If recovery fails, retain the journal, open Review / resolve, compare the recorded original and current values. If the GUI is unresponsive, close the owned process only if necessary, then reopen as the same Windows user. Restore the known original proxy settings manually if required; do not blanket-disable a preexisting proxy. Retain `%LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi` until restoration is verified. In an isolated VM, revert to snapshot if manual recovery is uncertain. Verify that the owned processes are gone, baseline connectivity is restored, and private configurations are not included in evidence. Report failures before further testing.

## Release decision

- Mandatory gates passed on exact artifact: NO (initial state).
- Critical defects open: unknown until execution.
- Stable release approved: NO.
- Final artifact and checksum: not produced.
