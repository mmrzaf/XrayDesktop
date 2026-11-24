# Xray Desktop — Software Design Document

**Document revision:** 1.0 draft · 2026-11-24  
**Product:** Xray Desktop (working name) · **Release scope:** v1.0  
**Platform:** Windows x64 · **Stack:** C# / .NET 10 LTS / WinForms  
**Core baseline:** Xray-core v26.11.9 (official release of 2026-11-09)  
**Status:** Design approved in principle; implementation and verification pending.

## 1. Purpose and principles

A small, portable desktop controller for an existing Xray client JSON configuration. The user supplies the official Windows x64 `xray.exe`, selects or pastes a complete configuration, starts the core, optionally enables the Windows user proxy, and can diagnose failures without a terminal or development tools.

- Xray owns protocols, transport, routing, DNS and config semantics. The GUI does not implement VLESS or recreate the core schema.
- Keep the user's config intact unless they explicitly edit and save it; never silently inject inbounds or rewrite routing.
- Core process state, local listener readiness, system proxy ownership, and remote connectivity are distinct facts.
- Prefer conservative failure behavior, clear explanations and a recoverable Windows proxy over automatic magic.
- No accounts, telemetry, ads, embedded web frontend, administrator privileges in normal operation, or installed development/runtime tools on the end-user machine.

## 2. Scope

| ID | Requirement | Priority |
|---|---|---|
| F01 | Detect adjacent xray.exe; retrieve version and test availability | Must |
| F02 | Import/open/paste/edit/format/save raw JSON or JSONC; multiple profiles | Must |
| F03 | Validate with Xray; start, stop and restart owned core | Must |
| F04 | Discover local HTTP/SOCKS proxy inbounds; select or override endpoint | Must |
| F05 | Independently enable/disable Windows user proxy; optional enable after start | Must |
| F06 | Restore original settings, journal recovery, detect unexpected exit | Must |
| F07 | Live stdout/stderr, error details, bounded session history | Must |
| F08 | Diagnostics with durations, explicit direct/proxy paths, export preview | Must |
| F09 | System tray, reopen window, explicit exit, status indicators | Must |
| F10 | Remember profile selection and preferences; clear local data | Must |
| F11 | Optional download-speed diagnostic with bounded traffic | Should; after required tests pass |

Out of scope: TUN, WFP, VPN/driver, kill switch, WinHTTP machine proxy, remote management, service installation, auto reconnect/failover, auto core downloads/updates, subscription feeds, VLESS share-link conversion, traffic counters, multiple simultaneous managed cores, mobile/macOS/Linux, automatic launch at login. No guarantee that apps that ignore Windows proxy settings are tunneled. The GUI is not an anonymity or leak-prevention product.

## 3. Compatibility and deployment

- Windows 11 x64 is the primary test platform. Windows 10 x64 is a compatibility target only for editions/builds supported by .NET 10; verify on a representative supported Windows 10 system before claiming support. No ARM64-native or x86 builds in v1.
- Build `net10.0-windows`, WinForms, self-contained `win-x64`, single-file publish; no third-party runtime packages. No trimming, Native AOT or ReadyToRun initially. Include native runtime libraries for self-extraction; Windows may use its temporary extraction directory. No separate .NET installation.
- Reference core is v26.11.9. Warn on other recognized versions, allow explicit use if validation works; unsupported versions are not certified. Detect version using the executable, not filename. Never claim compatibility simply because version parsing succeeded.
- The application never downloads/replaces the core. User obtains it and any required assets from trusted sources. Missing geo files are warnings until a config requires them; then surface Xray's actual validation error.
- The portable ZIP contains the GUI executable, README/quick start, example direct-only configuration, and SHA-256 manifest (no Xray binary, credentials, or real server configuration). Unsigned builds may trigger Windows SmartScreen; signing is not included in v1.

Expected folder after user setup:

```text
XrayDesktop/
  XrayDesktop.exe
  xray.exe            # supplied by user
  geoip.dat           # if required
  geosite.dat         # if required
  README.txt
  examples/
    direct-test.json  # no remote credentials
  data/               # created on demand
    settings.json
    profiles/
    runtime/
    logs/             # optional disk logging
```

Portable data is stored beside the app; writable folder required. If read-only, explain how to move the app rather than silently saving elsewhere. Exception: recovery state and per-user mutex live independently of the portable directory to allow recovery from another copy. Recovery location: `%LOCALAPPDATA%\XrayDesktop\Recovery` (per-user ACL, no profile credentials). Do not run from a shared/untrusted folder.

## 4. GUI and interaction

One resizable main form using standard WinForms controls; minimum practical size about 850×600, DPI-aware, keyboard accessible, no custom drawing or theme framework.

```text
 Xray Desktop                                    [Core: 26.11.9]
 Profile [ Home configuration                 v ] [Import] [New]
 Config  [ C:\...\home.json                      ] [Edit] [Validate]
 Core: Stopped / Running / Error    PID: —        Uptime: —
 [Start] [Stop] [Restart]   [ ] Enable system proxy on start
 HTTP [127.0.0.1:port ▼]    SOCKS [127.0.0.1:port ▼] [Override]
 Windows proxy: Off / Managed / External / Recovery required
 [Enable system proxy] [Restore system proxy]
 ----------------------------------------------------------------
 [ Logs ] [ Diagnostics ] [ Settings ]
 Time       Source     Level      Message
 ...
 [Copy redacted] [Save report] [Clear]               [Auto-scroll]
```

- Main form: profile selection, core version/PID, independent proxy control, actionable status text. Disable conflicting operations during transitions; allow cancelling probes. A running profile cannot be edited in-place: edits apply to next start, with a dirty/restart indicator.
- Editor dialog: plain multiline monospace text, paste, open, save, Save As, optional format, validation output. Strict JSON formatting may remove comments: require explicit confirmation for JSONC or refuse formatting it. Preserve encoding/content except intentional edits; unsaved changes prompt before close. Validate the actual editor snapshot, never implicitly overwrite source.
- Diagnostics tab: Test selected, Run all, Cancel, Copy/Export report; rows with outcome, elapsed time and details. Display test URL and whether it goes directly or through a proxy. Settings: startup behavior, endpoint choice/override, probe URLs/timeouts, log persistence and tray option.
- Tray: show window, start/stop, toggle managed proxy, exit. Close normally exits (restore then stop); minimize-to-tray is opt-in. Tray menu mirrors state; clear distinction between hide and exit.
- No auto start of core or enablement of proxy merely because GUI opens. Default enable-on-start checkbox is off, first use requires explicit action. No network probes on application startup.

## 5. Architecture and repository

Single process GUI, one owned child Xray process, small test harness, no services or IPC except Windows named mutex. Use a coordinator to serialize lifecycle transitions; use ordinary classes and a few test seams for OS effects. Separate pure logic from Win32 interop; no dependency injection framework, ORM, webview, or visual-designer generated code.

```text
XrayDesktop.sln
src/XrayDesktop/
  XrayDesktop.csproj, Program.cs
  UI/MainForm.cs, ConfigEditorForm.cs
  App/SessionController.cs, SessionState.cs
  Core/XrayProcess.cs, CoreVersion.cs, JobObject.cs
  Config/ProfileStore.cs, ConfigInspector.cs
  Windows/SystemProxy.cs, ProxyRecovery.cs, SingleInstance.cs
  Diagnostics/DiagnosticRunner.cs, NetworkProbes.cs, ReportRedactor.cs
  Storage/SettingsStore.cs, AtomicFile.cs, LogBuffer.cs
  Models/...
tests/XrayDesktop.Checks/     # no-framework console test runner
  fixtures/                  # synthetic, no secrets
scripts/                     # packaging and integration smoke tests
.github/workflows/build.yml
SDD.md, README.md, .gitignore
```

| Component | Responsibility / boundary |
|---|---|
| SessionController | Sole owner of transitions, core exit events and proxy acquire/release; cancellation/generation checks |
| XrayProcess | Version/validation/start/stop, argument list, stdout/stderr, process ownership |
| ConfigInspector | Best-effort read-only inspection of inbounds/endpoints; no protocol schema enforcement |
| ProfileStore | Explicit file import/save and managed profiles, immutable run snapshots |
| SystemProxy | Query/apply/refresh/verify current-user LAN proxy using WinINet; no network requests |
| ProxyRecovery | Durable snapshot and transaction journal; conflict-safe restoration |
| DiagnosticRunner | Cancellable, bounded network/local probes; independent clients for direct/proxy |
| LogBuffer / ReportRedactor | Bounded display, safe default exports, optional local logs |
| UI | Render state, send commands; no direct registry writes or process starts |

Async I/O and process waits never block UI. Marshal events onto the UI thread; batch log updates. Serialize start/stop/proxy operations (`SemaphoreSlim` or equivalent), avoid reentrant UI commands, use operation cancellation and session IDs so late callbacks cannot change a newer session. Dispose clients, process handles, streams and cancellation sources deterministically.

## 6. Configuration contract

1. Accept a single complete `.json` or `.jsonc` file or pasted JSON object. Multiple profiles are supported; merged config directories, YAML/TOML are not GUI features. Unknown keys survive unchanged. Allow comments and trailing commas in best-effort inspection; Xray is final authority. Duplicate/ambiguous inbound keys cause inspector warning rather than guessing. If inspection fails, show why and still allow Xray validation of raw bytes, with manual endpoint selection if appropriate.
2. Import creates a named profile reference or an explicit managed copy (offer choice); never edit external file without Save. Use collision-resistant profile IDs, not untrusted names as path components. Store selected profile and optional overrides separately from config. Renames do not affect source. Deleting a profile reference does not delete the original file.
3. When starting, capture exact saved bytes into a unique, private runtime snapshot, hash them, and validate and run the same snapshot. Use original profile directory as default child working directory, with explicit per-profile override; set `XRAY_LOCATION_ASSET` to the adjacent asset directory unless user overrides. Document relative cert/key/log/script paths and warn when copying may change their resolution. Verify relative path behavior against the target core in integration tests. Do not use shell or arbitrary CLI arguments. Remove inherited config-location overrides if they conflict with explicit file; preserve other environment deliberately.
4. Clean stale runtime snapshots only when known not in use. No copying of config into logs or report. Local JSON files and runtime snapshots are plaintext (Xray needs to read them); protect managed folders with current-user permissions where supported, warn for shared/non-NTFS storage. Encryption-at-rest for profiles is not promised.
5. Config validation result records exit code, duration and sanitized output. A successful validation means Xray can load the configuration, not that it has bound ports or connected to a server.

### 6.1 Core commands (target version)

```text
xray.exe version
xray.exe run -test -format json -c <absolute snapshot path>
xray.exe run -format json -c <absolute snapshot path>
```

Use `ProcessStartInfo.ArgumentList`, absolute executable path, `UseShellExecute=false`, redirected stdout/stderr, no console window. Do not use `cmd.exe` or shell quoting. A timeout is an explicit failed/unknown check, not a successful validation. Do not execute untrusted binaries/configs: a config can access files, open listeners, or use other core features; GUI is not a sandbox. Warn about TUN, non-loopback listeners and potentially privileged or script-related features; TUN configurations are not supported by this controller.

### 6.2 Inbound discovery and endpoint policy

- Read `inbounds[].tag`, `protocol`, `listen`, `port`, relevant authentication options. Candidates: HTTP and SOCKS. Xray's SOCKS inbound can also accept HTTP in supported modes; verify behavior on v26.11.9, never assume it solely from protocol name. HTTP and SOCKS capabilities are independently tested.
- Prefer explicit loopback `127.0.0.1` / `::1` and a single integer port 1–65535 (numeric strings only if safely interpreted). IPv6 URI host is bracketed. Wildcard/omitted/non-loopback listeners warn about exposure and are not auto-selected for Windows proxy. Port ranges, Unix sockets, unusual transports and ambiguous inbounds require manual selection or local-only mode.
- Multiple eligible inbounds: explicit selector (show tag and endpoint). Manual override must point at a loopback endpoint; it changes GUI targeting only, not the JSON. Do not silently choose an unrelated listener. No hard-coded required port.
- Windows proxy requires an unauthenticated HTTP-compatible endpoint verified as belonging to the managed core. Authenticated local inbounds may run, but v1 does not automatically set system proxy for them or persist local proxy credentials. SOCKS-only setups can run and be diagnosed; HTTP capability must be established to enable Windows proxy. If readiness/ownership cannot be established, disable system proxy and explain.
- Warn on port collision using preflight listener check; this is advisory, not race-free. Verify actual listener PID/ownership with Windows TCP owner table, or equivalent, after startup; handle IPv4 and IPv6. Do not probe by binding or terminate other processes. If core failed to bind, show its error.

## 7. Core lifecycle and state machine

Core states: `Missing`, `Checking`, `Stopped`, `Validating`, `Starting`, `Running`, `Stopping`, `Faulted`. Separately track proxy state: `Unmanaged`, `External`, `Acquiring`, `Managed`, `Restoring`, `RecoveryRequired`, `Conflict`. Readiness and internet test states are separate, with timestamps. Never use one green “Connected” label for all of these.

Startup: acquire per-user mutex (same across copies and terminal sessions where supported; fail closed if unavailable), reconcile recovery journal, check adjacent binary/version, load settings and profiles, inspect endpoint candidates and report existing Xray processes without killing them. A missing core does not block the editor or recovery UI. Do not change an existing external proxy without user action except recovery of our own recorded changes.

Start: freeze profile/endpoint selection → validate snapshot → preflight → spawn child → establish process ownership → wait for process alive and selected local listener owned by child → run local HTTP capability check if requested → optionally acquire system proxy. Configs without an eligible local inbound can be run in local-only mode (core running, readiness N/A). A running process with no ready listener is reported explicitly. Probe failure is not automatically interpreted as remote server failure. No public endpoint must be contacted merely to start a core; use bounded loopback protocol checks (e.g. synthetic closed-loop test with no public destination, where supported) and explicit external diagnostic actions. If capability is inconclusive, require a user-initiated proxied test before acquisition.

Stop: cancel diagnostics → restore any managed proxy and verify → stop only owned core → wait for exit → release handles and delete runtime snapshot. If restoration fails, offer Retry or explicit forced stop with warning and retain recovery journal. Restart performs stop followed by fresh validation/start; do not enable a different endpoint accidentally. Exit uses same stop flow. Unexpected core exit triggers immediate restoration while GUI lives; display exit code and error; no infinite restart loop.

Child ownership: keep process handle/PID plus start time; use Windows Job Object with `KILL_ON_JOB_CLOSE` and non-inheritable job handle for validation and running core where applicable. Prefer a race-free creation/assignment strategy if available; otherwise assign promptly, document/test the narrow pre-assignment crash window, and refuse managed launch if assignment fails. Never use process name alone to kill. Graceful stop via supported Windows mechanism only if verified; otherwise bounded termination of owned child/job. Do not advertise graceful shutdown until tested. Wait for stdout/stderr drain without deadlock; cap output. Core may write configured log files separately; GUI cannot promise capture of those logs.

## 8. Windows proxy management (critical subsystem)

Scope: current user's default LAN/Internet Options proxy, not machine-wide WinHTTP, VPN/RAS per-connection settings, browsers with custom configuration, DNS/UDP or system-wide network capture. Use Unicode WinINet `InternetQueryOption` / `InternetSetOption` with `INTERNET_OPTION_PER_CONNECTION_OPTION`; query flags, proxy server, bypass list, auto-config URL (and supported relevant flags). Use `INTERNET_OPTION_SETTINGS_CHANGED` and `INTERNET_OPTION_REFRESH`, check API return values, and read back settings. Do not edit binary connection blobs or set `netsh winhttp` values. Fail closed on policy-managed/unreadable settings or inability to capture/restore relevant state. Do not elevate to bypass policy.

### 8.1 Transaction

1. Verify owned, live, unauthenticated HTTP-capable loopback listener. Read current LAN options including PAC/WPAD/auto-detect flags. If a proxy/PAC already exists, show what will be temporarily replaced and require consent. If current state cannot be accurately captured, refuse acquisition.
2. Persist recovery journal atomically before touching Windows: schema version, user scope, transaction ID, UTC timestamp, previous values/flags, intended values, stage, endpoint, owner identity. Save with durable flush and atomic same-volume replace. Never overwrite an outstanding unresolved transaction. Restrict permissions. No plaintext config or secrets in journal; an existing PAC URL can be sensitive, so protect it and omit it from reports.
3. Apply manual HTTP proxy for HTTP/HTTPS (not a TLS proxy server), preserving or explicitly documenting bypass behavior; proposed default bypass `<local>` plus loopback as supported. Temporarily disable conflicting PAC/auto-detect, preserve their values for exact restore. Verify read-back and refresh. Mark acquired only after success. If partially applied, attempt safe rollback and keep journal if ambiguous.
4. On disable/stop/exit/child exit, query current state. If it matches our applied state, restore original flags/server/bypass/PAC and verify. If another app/user changed anything, do not blindly overwrite; show current/original/applied differences and require explicit decision. For partial writes, recover only when observed state can be attributed safely to the transaction; otherwise require review. Keep unresolved journal and status prominent. Clear journal only after verified restoration or explicit user decision to retain external settings.
5. On next launch, reconcile unfinished journal before new core/proxy operation, even if core missing or folder moved. If state already equals original, clear journal; if exact intended state remains, restore; otherwise present conflict. Do not touch an unowned stale proxy merely because it uses localhost. Provide a manual Windows Settings recovery guide in app/README.

**Hard-crash limitation:** No watchdog or service in v1. Job object should terminate owned Xray when GUI dies, but Windows proxy may point at a dead listener until next GUI launch/manual restoration. No promise of immediate cleanup after Task Manager termination, power loss, or OS crash. While GUI runs, detected child exits are handled immediately. A proxy transaction is not an OS-atomic compare-and-swap; outside writers can race, so re-read and report uncertainty. Store per-user recovery even across portable app copies; test concurrent instance behavior. If strong immediate crash recovery becomes mandatory, design a separate watchdog in a later revision.

## 9. Diagnostics and logging

All probes report `Pass / Fail / Warning / Skipped / Cancelled`, timestamp, elapsed ms, path, sanitized detail. Defaults are proposed and configurable: 5 s DNS/TCP, 10 s HTTP, 15 s config validation, 10 s listener readiness, 5 s version, 45 s overall suite; cap response bodies. Limit concurrency, never retry indefinitely. Run All is explicit; show destinations and warn direct tests reveal source IP and proxy tests follow config routing. Public IP test can be disabled, speed test separate opt-in. No credentials or real profile fixture in CI.

| ID | Probe | Implementation / interpretation |
|---|---|---|
| D01 | Core/version | File exists, process exit and version parsing |
| D02 | JSON inspection | Syntax/inspector compatibility; not substitute for core |
| D03 | Xray validation | Actual `run -test`; use same immutable snapshot |
| D04 | Remote endpoints | Best-effort known outbound server address/port extraction; unknown formats skipped, user can supply test endpoint; no blanket all-schema parsing |
| D05 | DNS | OS resolver for selected host (direct/local, not Xray DNS); IP literals skipped |
| D06 | TCP reachability | Direct bounded TCP handshake to chosen server address/port; not TLS, VLESS or UDP/QUIC validation |
| D07 | Local listener | Port availability when stopped; owned PID and TCP readiness when running |
| D08 | HTTP proxy | Explicit HTTP proxy and HTTPS request with normal TLS verification; no direct fallback |
| D09 | SOCKS5 proxy | Explicit socks5 proxy; verify .NET handler behavior and no direct fallback in tests; authenticated inbounds unsupported in v1 |
| D10 | Direct internet | `SocketsHttpHandler.UseProxy=false`, HTTPS test; does not alter OS proxy; may bypass intended proxy, explicit consent |
| D11 | Proxied internet/latency | Same configurable HTTPS destination via chosen local proxy; report HTTP status and full request elapsed time, not network RTT |
| D12 | Public IP | Direct and proxied HTTPS requests to same configurable IP service, parse IP, show comparison; equal IP does not prove failure, different IP does not prove all traffic proxied |
| D13 | Windows proxy | Query flags/endpoint/bypass/PAC presence, compare with ownership journal; do not disclose sensitive URLs |
| D14 | Process health | Owned PID, uptime, exit code, last relevant errors; no automatic kill |
| D15 | Optional speed | Explicit bounded download (e.g. 5 MiB, 20 s) through proxy only; measured throughput, not line capacity; defer if release risk |

Proposed endpoints: `https://www.gstatic.com/generate_204` (expected 204) and `https://api.ipify.org` (IP result). Configurable, displayed before execution; no claim that services are reachable from all networks. For direct/proxy comparison use same endpoint, disable redirects by default, distinguish HTTP status, DNS, TLS and timeout; keep TLS certificate validation on. Use separate handlers and `WebProxy` with explicit URI; no implicit system proxy, environment proxy, bypass or direct fallback in explicit proxy probes. Validate via synthetic listener tests. SOCKS DNS behavior depends on handler/core; report what is measured, not unverified leak claims. A successful proxied request can still take a direct outbound selected by Xray routing. Skip dependent probes when prerequisites fail; allow independent tests while core is stopped. Network failures never alter configuration or switch routes automatically.

Logs: consume stdout/stderr asynchronously; preserve source and timestamp; classify severity best effort; buffer max 5,000 lines/4 MiB and cap individual lines. Batch UI updates, no freezes. Disk logs disabled by default; if enabled rotate three 5 MiB files and label as potentially sensitive. Core-configured file logs are not automatically collected. No automatic dumping of JSON, command args containing secrets, or remote traffic data.

Report export defaults to an allowlisted structured report: app/core versions, OS build, relevant state, diagnostic outcomes, durations, error categories. Omit raw configs, arbitrary log text, local filesystem paths, remote host/IP, public IP, PAC URLs, tokens, UUIDs, keys and usernames by default. Optional selected log excerpts are best-effort redacted against known secrets and patterns, then shown in a preview; cannot guarantee arbitrary third-party output is secret-free. Raw local logs are visible to the user but copying raw requires separate explicit confirmation. No automatic upload.

## 10. Persistence and robustness

`settings.json` versioned: profile IDs/paths, active profile, working directory/endpoint overrides, enable-on-start preference, tray preference, probe endpoints/timeouts, log options, window bounds. No secrets outside user-supplied profiles. Atomic temp + replace with bounded backup; corrupted settings produce warning and safe defaults, not automatic core launch or journal deletion. Validate sizes (proposed config maximum 10 MiB), paths, URL schemes and ports. Treat imported data as untrusted. Keep logs and data bounded; failures to persist proxy journal prevent acquisition, while optional log disk errors do not stop core. Single-instance mutex must guard both settings and OS proxy; per-user scope avoids cross-user interference.

## 11. Testing strategy and acceptance criteria

No third-party packages for runtime or tests: small deterministic console assertion runner with nonzero failure exit, plus PowerShell integration smoke tests on Windows. Fake proxy backend and fake process/clock where useful; synthetic Xray fixture uses loopback HTTP/SOCKS and `freedom` outbound to a local HTTP test server (no real remote server/credentials). Use dynamically selected test ports and handle races. Test actual core v26.11.9 on Windows. Do not modify a developer's real proxy settings in automated tests by default; native proxy tests are opt-in, isolated Windows VM/test account with cleanup in finally and external recovery instructions.

| Test | Release acceptance |
|---|---|
| A01 Missing core, wrong arch, version timeout | App opens, useful error, no proxy changes; editor/recovery usable |
| A02 Invalid JSON, invalid config, missing geodata | Core error surfaced, no proxy acquired, original bytes intact |
| A03 Profile editing and unknown keys | Explicit saves only; cancel/dirty handling; comments/Unicode/spaces and relative paths tested |
| A04 Dynamic HTTP/SOCKS endpoints | Correct detection/selection, manual override, mixed capability verified; auth/nonlocal/ambiguous rejected for auto proxy |
| A05 Port collision and unrelated Xray | No unrelated process killed or proxy directed to it; error shown |
| A06 Start/stop/restart races | Only one owned child/session; no blocked UI; stale callbacks ignored; bounded cancellation |
| A07 Proxy off/on independent | Start local-only leaves existing proxy untouched; valid acquisition and verified restore, repeat cycles |
| A08 Existing manual/PAC/WPAD settings | Consent and exact restore of captured semantics; policy/read/write/refresh failure visible |
| A09 Partial transaction and external mutation | Journal survives errors; no blind overwrite; recovery works from second portable copy |
| A10 Core killed while running | GUI observes exit, restores owned proxy, reports error |
| A11 GUI force kill | Owned child exits where job assignment complete; next launch restores owned proxy; documented crash interval |
| A12 Real local diagnostics | Direct versus proxy path proved by fake listener/counters; unreachable proxy never falls back direct; cancellation, TLS and HTTP errors visible |
| A13 Export security | Fixtures with UUID/key/token/URL/path/IP never appear in default report; optional raw requires warning |
| A14 Packaged EXE | Launch on clean Windows 11 x64 without installed .NET SDK/runtime, non-admin; locate adjacent core and assets; paths with spaces/Unicode |
| A15 Real VLESS config | User-provided private config validates, connects, browser uses Windows proxy when enabled, restore verified; no config in GitHub |
| A16 GUI/tray/DPI | Responsive during validation/log flood/diagnostics, keyboard navigation, 100/150/200% DPI, exit semantics |
| A17 Windows 10 | Representative supported edition tested before claiming compatibility |

Separate automated results from manual acceptance; record actual OS build, core version and test outcome. A green CI run alone does not satisfy A08–A11, A14–A17 unless those exact scenarios were actually exercised. Do not declare stable with unresolved critical proxy/process defects. If a manual check cannot be performed, label build preview and document it.

## 12. Build, packaging and release pipeline

One GitHub Actions workflow initially; Windows hosted runner pinned to a concrete supported image (e.g. `windows-2025`, verify availability), .NET SDK pinned to a specific available 10.0 servicing version via `global.json` at implementation, action versions/commit SHAs pinned after verification. No floating core releases, no cache initially, no matrices, no installer/signing workflow. Permissions read-only for CI; release publishing separate and explicit. PRs and pushes run the same checks, plus manual dispatch.

1. Checkout; set up SDK; print `dotnet --info`; restore solution for the declared runtime/target; use lock files/locked mode after initial lock generation if needed. No third-party package restore beyond SDK/runtime packs.
2. Build Release with no restore; nullable and analyzers enabled; treat warnings as errors for owned code after baseline established. Run the console test harness with explicit project and nonzero failure propagation.
3. Obtain pinned core from official GitHub release for integration tests only, verify release asset checksum using pinned trusted digest/checksum file, extract to CI temp. Confirm version and run fixture tests. No public endpoint dependency for functional CI. Fail clearly if official asset unavailable; do not silently skip required integration checks or replace with latest.
4. Publish GUI with `dotnet publish src/XrayDesktop/XrayDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o <staging>` (restore/publish properties must match; confirm actual outputs). Do not rely on cross-compilation as proof of runtime behavior.
5. Assert staged EXE exists; run `--self-test` or `--version` headless path that exits without changing proxy; fail on nonzero or timeout. Package only expected files, verify ZIP contents, compute SHA-256, upload artifact and build/test summaries. Test GUI behavior separately on real desktop. Never upload CI core, test logs containing secrets, private config, or build caches.
6. Release only after manual gate: tag `v1.0.0`, attach verified portable ZIP and checksums plus known limitations. User downloads and unzips; no C# tools required.

## 13. Implementation plan (ordered, gated; no time promises)

| Phase | Work and deliverable | Exit gate |
|---|---|---|
| P0 Bootstrap | Repo, solution, SDK pin, plain form, CLI version/self-test, first CI publish | Green clean build; packaged EXE launches/headless smoke |
| P1 Core control | Profiles, editor, inspector, version/validate, run snapshots, lifecycle, job ownership, bounded logging | A01–A06 automated/integration; no system proxy writes yet |
| P2 Proxy safety | Native wrapper, fake backend, transaction journal, restoration/conflict recovery, independent toggle | A07–A11 on isolated Windows test account; no critical defects |
| P3 Diagnostics | Explicit direct/HTTP/SOCKS probes, remote DNS/TCP, latency/IP, cancellation, export | A12–A13; no direct fallback; sensitive export tests |
| P4 UX and packaging | Tray, DPI, settings robustness, docs, ZIP/checksums, error messages | A14/A16, repeatable clean CI; optional speed only if time/risk permits |
| P5 User acceptance | Private v26.11.9 config test, browser/system proxy test, crash/restart/recovery checklist, Win10 if available | A15/A17 as applicable, all required gates evidenced; release candidate promoted |

Build strategy: establish one green minimal pipeline first; avoid introducing UI, native interop and packaging all in one untested change. Each phase is a small reviewable change with tests. If CI fails, inspect complete failing command/log, reproduce with same pinned inputs, change root cause rather than guessing or bypassing tests. User should only need to provide a private config and run a packaged build for Windows acceptance; never post credentials to a repository or issue.

## 14. Risks, decisions and open verification

| Risk / decision | Mitigation / status |
|---|---|
| Native WinINet marshaling or policy | Isolate wrapper, Unicode structs, correct buffer freeing, API error codes, fake backend + real VM tests |
| GUI hard crash leaves proxy enabled | Persistent journal and next-launch recovery; no watchdog in v1 (known gap) |
| Process orphan before job assignment | Prefer atomic assignment, test ownership and failure; narrow race if standard process launch used |
| External proxy writer races | Re-read, journal and conservative conflict handling; no blind restore |
| Core evolution | Pinned v26.11.9 integration; warn other versions, delegate semantic validation to core |
| Real routing differs from intended VLESS | Probes report actual path used by app and observed IP, not guarantee of upstream or whole-machine tunnel |
| User config secrets | Local plaintext warning, ACL where supported, no telemetry, allowlisted export, never upload config |
| Windows 10 / signing | Verify supported editions; unsigned SmartScreen warning; no certification yet |
| Public probes blocked | User-selected URLs, explicit timeouts, distinguish test endpoint failure from core failure |

Design assumptions added in this draft: working name, primary Windows 11 target, data folder and per-user recovery exception, conservative proxy eligibility, default local-only startup, opt-in network tests/disk logs, no watchdog/auto reconnect, plain JSON editor, optional speed diagnostic, and no test framework dependency. These are changeable before implementation. Exact SDK patch, release asset name/digest, .NET SOCKS behavior, Xray relative-path and mixed-inbound behavior, WinINet restoration semantics and Windows runner support must be verified during implementation. No source code was compiled, no repository was created and no Windows behavior was tested as part of writing this document.

## 15. Primary references

Checked 2026-11-24. References establish available APIs/CLI, not proof of this application's implementation.

- Xray v26.11.9: https://github.com/XTLS/Xray-core/releases/tag/v26.11.9
- Xray tagged CLI source: https://github.com/XTLS/Xray-core/blob/v26.11.9/main/run.go
- Xray inbound docs: https://xtls.github.io/en/config/inbound.html
- Xray SOCKS inbound: https://xtls.github.io/en/config/inbounds/socks.html
- Xray HTTP inbound: https://xtls.github.io/en/config/inbounds/http.html
- WinINet options: https://learn.microsoft.com/en-us/windows/win32/wininet/setting-and-retrieving-internet-options
- WinINet flags: https://learn.microsoft.com/en-us/windows/win32/wininet/option-flags
- Windows Job Objects: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
- .NET single-file deployment: https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- .NET Windows support: https://learn.microsoft.com/en-us/dotnet/core/install/windows
- GitHub Actions .NET: https://docs.github.com/en/actions/tutorials/build-and-test-code/net
- GitHub Actions artifacts: https://docs.github.com/en/actions/tutorials/store-and-share-data
