Xray Desktop 0.1.1 - portable preview (acceptance pending)
==========================================================

Windows x64. Extract the entire archive into a writable, private folder. The
archive already contains the official Xray-core v26.11.9 (xray.exe), geoip.dat
and geosite.dat. No .NET runtime, separate Xray download, development tools or
admin elevation is required. Unsigned builds may trigger SmartScreen.

Launch XrayDesktop.exe, then import an existing raw JSON/JSONC configuration as
a reference or managed copy, or create one with New. Edit and explicitly save
changes; Save As makes another profile. The working directory is the original
profile directory (a Save As uses the new directory). Relative paths and
required assets must be verified. Validate calls the bundled Xray, not a
replacement schema. Start validates a snapshot, then starts that same snapshot.
Stop terminates the owned job, not other Xray processes. Choose an explicit
local inbound or a manual loopback endpoint to check TCP listener ownership.
Listener ownership does not establish HTTP/SOCKS capability or remote
connectivity.

Replacing xray.exe or the geodata files with another compatible build is an
optional advanced operation. The app detects the adjacent executable, checks
its version and warns when it is outside the tested baseline. Missing bundled
files produce a clear error. See THIRD-PARTY-NOTICES.txt and licenses/ for the
bundled core and geodata provenance, licenses and source availability.

After Start, choose Enable system proxy and approve a single HTTPS test to
the Diagnostics HTTPS destination (default https://www.gstatic.com/generate_204)
through the selected owned HTTP proxy. On success, the
app manages the current user's default Windows LAN proxy. Local/intranet hosts
bypass it. SOCKS-only or known authenticated endpoints are not eligible. The
optional enable-after-Start checkbox is off by default, lasts for this GUI
session and still asks before the test. Restore system proxy can leave Xray
running. Stop/Restart/Close restore first; a failed restore requires explicit
confirmation before force stop. Restart does not automatically reacquire proxy
unless enable-after-Start is selected and the user approves again.

Start, Logs, Diagnostics and Settings are separate tabs. The Start tab is
numbered: choose a configuration, optionally choose the listener to verify,
validate/start Xray, then optionally enable the Windows proxy. A status header
on every tab says what is happening, what to do next and shows the last error.
The tray icon shows core and proxy state; right-click for Show, Start, Stop,
Enable, Restore and Exit. Double-click to reopen. Minimize-to-tray is off by
default; opt in under Settings and save desktop preferences. Closing the window or choosing
Exit still restores and stops the owned core. Tray commands reopen the window
so confirmations remain visible. While busy, show the window and use Cancel
operation; cancelling a network probe does not discard proxy recovery.

Settings can remember window bounds and log auto-scroll, reset desktop/probe
preferences, or clear profile references (never delete actual JSON files).
Unreadable settings are retained until explicit replacement. Desktop settings
are local to data/desktop.json. No automatic disk recording. Copy or save raw
logs only after reviewing the sensitive-data warning.

On abnormal GUI termination, Windows may still reference the dead listener.
Reopen under the same Windows user; recovery runs before core detection.
Use Review / resolve if another application changed settings; review the
original, intended and current settings locally (they may contain secrets).
If recovery is impossible, open Settings > Network & Internet > Proxy and
restore the previously known manual proxy, script and auto-detect values.
Do not disable a pre-existing corporate proxy indiscriminately. The encrypted
journal at %LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi is not in
the portable folder; do not delete it unless manual recovery is verified.
An unreadable journal can be moved to a private backup after all app copies
are closed and manual recovery is confirmed.

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
