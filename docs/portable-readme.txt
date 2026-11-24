Xray Desktop - Phase 0 development preview
==========================================

Windows x64. Extract the entire archive into a writable folder and open
XrayDesktop.exe. The application does not need a separately installed .NET
runtime. This unsigned preview may trigger Windows SmartScreen; use only
artifacts from the trusted repository and verify their checksums.

This build contains the application shell only. It does not launch Xray,
manage configurations, change proxy settings, or perform network diagnostics.
The compatibility target is Xray-core v26.11.9. No Xray binary is included.

The examples/direct-test.json fixture is for future integration testing.
It uses loopback HTTP/SOCKS listeners and a direct outbound: it is NOT a VPN,
VLESS configuration or privacy-protecting connection. It has no credentials.
The Phase 0 GUI does not load or run it.

SHA256SUMS.txt lists the SHA-256 checksums of the other archive files.
The separate .zip.sha256 file covers the complete archive. Checksums detect
corruption; they are not a code signature or proof of publisher identity.

Headless options (for automated checks):
  --version    Print the application version, then exit.
  --self-test  Check basic Windows x64 bootstrap metadata, then exit.
  --help       Print usage, then exit.

The preview must not be considered a functional Xray client or a stable
release. See the repository's SDD.md for the remaining implementation gates.
