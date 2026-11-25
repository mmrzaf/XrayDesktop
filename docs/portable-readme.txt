Xray Desktop 0.4.0 - portable (manual acceptance pending)
==========================================================

Windows x64. Extract the entire archive into a writable, private folder. The
archive already contains the official Xray-core v26.11.9 (xray.exe), geoip.dat
and geosite.dat. No .NET runtime, separate Xray download, development tools or
admin elevation is required. Unsigned builds may trigger SmartScreen.

GETTING STARTED
1. Copy a vless://, vmess://, trojan:// or ss:// link and choose
   Add > Paste link from clipboard. (Or Add > Import file..., or drag a .json
   file onto the window.) The bundled Xray checks the profile before saving it.
2. Click Connect. The app starts Xray, tests the server with one HTTPS request
   to https://www.gstatic.com/generate_204 (changeable on the Diagnostics tab),
   and only then sets the Windows proxy. If the test fails, Windows is not
   touched and the window explains what went wrong.
3. Click Disconnect when you are done. Your previous Windows proxy settings are
   restored exactly, then Xray stops.

Windows needs an HTTP proxy port. If your profile has only SOCKS, or its ports
are busy, the app adds or moves a local listener in the copy it runs; the
profile file itself is never changed.

While connected, a small request every 20 seconds checks the server. If it
stops answering the status becomes "Connection problem". Closing the window
while connected keeps the connection running in the notification area; use the
tray menu to disconnect or exit. Starting the app again simply shows the
running window. Exit, log off, shutdown and an unexpected Xray exit all restore
your Windows proxy settings first.

The More menu edits (validated by Xray), renames or removes a profile and
reviews the Windows proxy state. Shortcuts: F6 connect/disconnect, Ctrl+V paste
link, Ctrl+O import, Ctrl+E edit, Ctrl+L logs, Ctrl+D diagnostics, Ctrl+,
settings, Ctrl+F find in logs. Raw logs may contain secrets; copy or save them
only after reviewing the warning. Settings and profiles live in the data folder
next to the app, which therefore must be writable.

Replacing xray.exe or the geodata files with another compatible build is an
optional advanced operation. The app checks the version and warns when it is
outside the tested baseline. See THIRD-PARTY-NOTICES.txt and licenses/ for the
bundled core and geodata provenance, licenses and source availability.

IF THE INTERNET STOPS WORKING AFTER THE APP CRASHED
A guard process normally restores your settings within seconds, and the next
launch of the app restores them too. If neither happened, open Windows
Settings > Network & internet > Proxy and set back your previous values. The
encrypted record of your original settings is at
%LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi (not in the portable
folder); keep it until your settings are verified. If another program changed
the proxy settings while the app was using them, the app asks before touching
anything: use More > Review Windows proxy to compare the original, the app's
and the current values (they may contain secrets).

Diagnostics: Run selected (default Local) or Run all. Configure HTTPS and IP
service URLs and a 1-15 second network timeout. For direct DNS/TCP/HTTPS,
explicitly check Allow direct; the destination may see your source IP. Enable
public IP lookup separately. Proxied requests follow your Xray routing; a
successful request or changed IP does not establish system-wide protection.
Local details may show private addresses. Preview / export report reconstructs
only fixed metadata and excludes raw config, URLs, addresses and logs. Save
probe destinations locally only if you want them persisted; consent is never
persisted. Diagnostics run sequentially with a 45-second suite budget, plus
bounded native cleanup. Use Cancel operation to stop. For more, see docs/phase-3.md
in the source repository.

No TUN, WinHTTP machine proxy or speed test. The example
uses a direct outbound; it is NOT a VPN or privacy-protecting connection.
No third-party credentials or public requests are needed for default checks.

Configurations and snapshots are plaintext in the app data directory; keep
this directory private. Raw logs may include secrets. Copy and save raw logs require
confirmation. Runtime snapshots are removed on normal stop; a crash can leave
stale snapshots. After verifying no owned core is active, remove stale files
manually. No background cleanup or telemetry is implemented.

Headless: --version, --self-test, --help. The self-test checks bootstrap
metadata, not core functionality. SHA256SUMS.txt checks the archive payload;
checksums do not prove publisher identity. See THIRD-PARTY-NOTICES.txt and
licenses/ for bundled third-party licensing. See the repository and
docs/phase-5.md for test status.
This is an unapproved release preview, not a certified stable release.
First proxy-write acceptance must be performed in a disposable Windows test account/VM.
