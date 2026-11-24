# Phase 5 — portable preview and acceptance status

**Preview:** `0.2.1` (source only). **Stable release:** NOT APPROVED.

The SDD requires actual Windows build and runtime evidence. Prior source previews did not satisfy their exit gates. This phase prepares a preview build, not a claim of acceptance. No stable tag, signed executable, or release attachment has been created.

## Changes

- CI requires the official Xray-core v26.11.9 Windows x64 integration asset. `scripts/Get-TestCore.ps1` downloads from the official XTLS release, verifies the pinned archive digest before extraction, and fails on missing or mismatched bytes. The digest `1262522266337b824f07beb8e1375786b437683b3890105469088f8e17e0a69d` was independently recorded from [ScoopInstaller/Main's Xray manifest](https://github.com/ScoopInstaller/Main/blob/master/bucket/xray.json) for this exact release. [Official release](https://github.com/XTLS/Xray-core/releases/tag/v26.11.9). No downloaded bytes were available in the authoring environment to independently validate the digest; CI must perform this check.
- CI requires the real-core test and the read-only WinINet query. A missing required core fails the harness; local source builds may still skip if not explicitly required. CI does not intentionally mutate Windows proxy settings.
- Real-core integration now exercises malformed-config rejection, HTTP and SOCKS proxy requests to an in-process loopback HTTP origin, owned listener verification, normal stop, unexpected core exit and restart. No private VLESS account or public service is required. Existing fake-proxy tests cover no-fallback behavior and TLS rejection separately.
- HTTPS proxy acquisition now distinguishes an internal verification deadline from caller cancellation. The new loopback regression check exercises both conditions. The acquisition deadline remains 10 seconds in production.
- Preview packaging still uses the pinned SDK, read-only CI, no third-party application packages, and extracted executable checks. The build summary distinguishes skipped optional tests from passed checks. Hidden native-form construction remains opt-in; interactive desktop acceptance is mandatory before promotion.
- The portable package now bundles the official v26.11.9 `xray.exe`, `geoip.dat`, `geosite.dat`, license texts and third-party notices. Packaging verifies the archive and file hashes, byte identity with the verified distribution, the extracted core's version output, the extracted GUI headless checks and a real-core integration run from the extracted package. No Xray binary or geodata is committed to Git.
- The main window now uses a persistent status header (current state, next action, last error), numbered workflow sections on the Start tab, prominent primary actions, and an inline error banner with direct access to Logs and Local diagnostics. Validation output is shown on the Start tab, port conflicts are called out before Start, and SOCKS-only profiles can be copied into a validated managed profile with an HTTP listener. Validation messages state whether Xray accepted the configuration instead of only reporting an exit code.

## Evidence available now

- Repository integrity, previous history, author identity, working tree and tracked whitespace: checked in the source environment before export.
- JSON, XML, YAML, project references and version/artifact consistency: structural checks only.
- Windows compilation, 77 mandatory console checks, real-core integration, read-only WinINet, five packaged headless checks: **not run**. The current source environment is Linux with no .NET SDK or PowerShell; Microsoft SDK download failed DNS resolution. There is no configured remote or accessible project repository.
- Native WinINet mutation, external-conflict resolution, crash recovery, clean-machine launch, interactive GUI/DPI and private VLESS: **not run**.

## Promotion procedure

1. Push candidate history to an authorized private repository; run the Windows build on the candidate commit. Investigate and fix real failures in focused commits without weakening checks. Retain the run URL, exact commit SHA, SDK version and archive SHA-256.
2. Download the candidate from the successful run. Perform [manual acceptance](acceptance-checklist.md) in an isolated Windows environment with known original proxy settings. Then test a trusted private VLESS profile without putting it or raw logs into CI.
3. Record each A01–A17 result with evidence and environment details. All applicable mandatory criteria must pass; any unsupported Windows edition must remain explicitly unclaimed. No critical proxy/process defect may remain unresolved.
4. Only after acceptance, change the shared version to the intended stable version, repeat the same build and relevant smoke/manual checks on that exact final artifact, create the matching `v*` tag and publish the verified ZIP/checksum after explicit authorization. Do not promote this untested candidate as-is.

Remaining design limits: unsigned executable; no watchdog, so GUI force termination can leave the system proxy until the next launch or manual recovery; WinINet transactions are not atomic against external changes; plaintext configurations and stale snapshots require private local storage; Windows 10 support remains unverified. Optional speed test and rotating disk logs are outside this candidate. No new feature scope.

## Subsequent CI evidence

The first Windows run at `656422d` compiled both projects with zero warnings and errors, passed 76/77 mandatory checks and the native WinINet read-only check. The TLS fixture timed out and real-core integration reported access denied for `xray.exe`; packaging did not run. See [CI and release workflow](ci-release.md) for the targeted fixes and pending rerun. This does not satisfy stable acceptance.
