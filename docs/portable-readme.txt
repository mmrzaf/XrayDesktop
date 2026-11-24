Xray Desktop - Phase 1 development preview
==========================================

Windows x64. Extract the entire archive into a writable, private folder.
Obtain a trusted xray.exe (reference version v26.11.9) and put it next to
XrayDesktop.exe, along with any geodata files your config requires. No .NET
runtime or admin elevation is intended. Unsigned builds may trigger SmartScreen.

Open the GUI. It checks the adjacent core, but does not auto-start it. Import
an existing raw JSON/JSONC configuration as a reference or managed copy, or
create one with New. Edit and explicitly save changes; Save As makes another
profile. The working directory is the original profile directory (a Save As
uses the new directory). Relative paths and required assets must be verified.
Validate calls Xray, not a replacement schema. Start validates a snapshot,
then starts that same snapshot. Stop terminates the owned job, not other Xray
processes. Choose an explicit local inbound or a manual loopback endpoint to
check TCP listener ownership. Listener ownership does not establish HTTP/SOCKS
capability or remote connectivity.

Phase 1 does not change Windows system proxy settings, support TUN, or perform
public internet probes. The example direct-test.json uses a direct outbound;
it is NOT a VPN or privacy-protecting connection. Stop/Restart/Close are local
core controls only. No third-party credentials are needed for automated checks.

Configurations and snapshots are plaintext in the app data directory; keep
this directory private. Raw logs may include secrets. Copy raw logs requires
confirmation. Runtime snapshots are removed on normal stop; a crash can leave
stale snapshots. After verifying no owned core is active, remove stale files
manually. No background cleanup or telemetry is implemented.

Headless: --version, --self-test, --help. The self-test checks bootstrap
metadata, not core functionality. SHA256SUMS.txt checks the archive payload;
checksums do not prove publisher identity. See the repository for test status.
This is a development preview, not a certified stable release.
