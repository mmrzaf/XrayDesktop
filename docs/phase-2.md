# Phase 2 — Windows proxy transactions and recovery

Source preview: `0.1.0-preview.2`. Xray baseline: v26.11.9. Phase 0, 1 and 2 Windows acceptance gates are open. No tested Windows executable or native write/restore evidence is claimed.

## Implemented scope

- Current-user default LAN connection only; Unicode WinINet per-connection options. Read UI flags (direct/manual/PAC/WPAD), server, bypass and PAC URL; restore via `INTERNET_PER_CONN_FLAGS`. Notify settings-changed and refresh, then verify the returned configuration. No registry writes, WinHTTP changes, machine proxy control, RAS/VPN or elevation. Policy detection is conservative and read-only; known proxy policies or uncertain scope block writes.
- Independent proxy controls with clear `Unmanaged`, `External`, `Managed`, `Unavailable`, `RecoveryRequired` and `Conflict` states. Status is periodically inspected when no command is in progress. The one selected runtime endpoint is frozen from the validated session. Explicit loopback HTTP only; known auth and SOCKS-only endpoints cannot acquire. Manual HTTP endpoints must pass the same owned-listener and anonymous HTTPS test.
- One explicit HTTPS request to `https://example.com/` via the selected proxy, with a 10-second deadline, no automatic redirects, cookies, default credentials or direct proxy fallback. The user consents each time; no probe on opening the GUI, and default Start is local-only. Successful request demonstrates current anonymous HTTP CONNECT capability to this particular destination, not remote IP, latency monitoring or comprehensive connectivity. Failure leaves the core running but does not enable Windows proxy. The optional enable-after-Start checkbox is session-only and defaults off.
- Prepare a durable encrypted journal before mutation; compare reviewed and current settings before writing and verify owner before/after. If no mutation was attempted, discard the prepared journal without a proxy write. Partial or ambiguous writes retain the journal and require review. Exact original/applied matches permit conservative rollback. The journal contains schema, transaction ID, current SID, UTC time, original and intended values and a prepared marker. `Managed` is an in-memory fact only after successful notification, read-back and ownership checks; restart reconciles from actual OS values instead of trusting a persisted completion flag. No second journal can replace an unresolved one.
- Storage: `%LOCALAPPDATA%\XrayDesktop\ProxyRecovery\pending.dpapi`, independent of the portable folder, using DPAPI current-user scope, inherited per-user directory ACL and same-volume atomic move after flushing file data. Journal metadata/values are encrypted at rest; no plaintext PAC URL in recovery logs or CI output. The existing per-user mutex serializes app copies. This is not a defense against hostile same-user software.
- Restore prior values before Stop/Restart/Close; an explicit force-stop confirmation retains recovery if verification fails. Unexpected child exit attempts restore before releasing handles. Startup attempts recovery before core detection, even if the core is missing. No new managed core starts while a recovery journal is pending/unreadable. No journal means no ownership: never clear an external localhost proxy automatically.
- External edits are not overwritten automatically. Review compares original, intended and current values in a local sensitive-data dialog; explicit restore or keep-current actions verify that the reviewed state has not changed. Keeping current settings is relinquishment, not restoration, and may leave an external proxy depending on Xray.

## Boundaries and deviations from the SDD

- No watchdog. After GUI force-kill, power failure or OS crash, Windows may reference a dead listener until the next launch or manual restoration. An unexpected child exit is handled on the next idle UI poll (300 ms polling interval), not while a modal operation or bounded probe holds the command gate. The OS calls themselves are synchronous on a worker; the transaction cannot be interrupted halfway by Cancel. A hung native API could delay command completion. The inherited job assignment crash window remains.
- `WinINet` has no atomic compare-and-swap. Another proxy manager can change settings between read/write calls or coincidentally apply exactly the intended values. Exact-value checks mitigate but cannot eliminate races. Any unexpected normalization or mixed state is treated as conflict/recovery, not success. Native verification of flags and Windows behavior remains mandatory. Null and empty native string values are normalized as equivalent; no claim of byte-for-byte registry restoration or preservation of transient WPAD cache state. The dormant PAC URL is retained in the managed state while PAC and auto-detect flags are disabled. Managed bypass is `<local>;localhost;127.0.0.1;[::1]`.
- Policy detection covers known registry policy locations and scope restrictions; unknown MDM/policy configurations are not comprehensively discoverable. No policy bypass. Unknown flags, invalid values and API failures fail closed. If external settings are changed during a transaction, the journal is retained unless the user explicitly relinquishes it. If other same-user software deletes the journal, automatic restoration cannot be guaranteed.
- Journal ACL inherits the user's LocalAppData ACL; no explicit ACL rewrite. DPAPI is not portable to another account or guaranteed recoverable after user profile/key loss. Atomic file-data flush and rename are not a proof of durability across all filesystem/hardware failures. Corrupt or unreadable journals are retained. After manual restoration, closing all instances and moving an unreadable journal to a private backup is a documented last resort.
- No TUN, kill switch, general diagnostics or tray in this phase. No claim that every application respects the proxy. The default direct example is not a privacy tool. Profile files and raw Xray logs remain plaintext/private as documented in Phase 1. Proxy test URL is fixed for this phase; configurable probe endpoints belong to Phase 3.

## Automated checks

Default build runs the 20 existing checks plus 24 Phase 2 checks (44 mandatory checks total), and five extracted-EXE smoke checks. Proxy transaction tests use in-memory fake system and journal backends: order, no-journal behavior, stale reviews, conflict, explicit keep/restore, policy failures, interrupted writes, refresh failure, ownership loss, retry and corrupted recovery. The x64 native struct layout check requires Windows x64, DPAPI uses a temporary directory, and the loopback CONNECT rejection check does not forward traffic. None deliberately writes actual Windows proxy settings. Optional `XRAY_TEST_WININET_READ=1` reads real WinINet values without logging them, and `XRAY_TEST_CORE` still enables the separate real-core check. Neither is proof of native mutation/restoration. Execution remains pending.

## Native acceptance procedure (disposable interactive Windows account / VM)

Run the pinned CI build first. Use a VM checkpoint or disposable account with known proxy settings, no corporate proxy requirements and the actual Xray v26.11.9 executable. Record Windows build, application version, Xray version, check outcomes and whether each field was restored; do not copy private URLs into reports.

| Case | Expected behavior |
| --- | --- |
| Clean startup, missing core, no journal | No system writes; editor/review available |
| Direct-only config, no eligible HTTP | Core can run locally; enable refused |
| Unauthenticated HTTP listener with successful explicit probe | Journal exists before settings change; flags/manual server/bypass/PAC match intent |
| Authenticated/SOCKS-only endpoint, wrong PID, failed probe, cancel | No system proxy change |
| IPv4 and IPv6 loopback | Native application/read-back verified for each; unsupported cases remain local-only |
| Existing manual proxy, disabled manual fields, PAC, WPAD, combinations | Consent before acquire; original editable flags and fields restored exactly after stop |
| Restore while core running | Proxy restored; same Xray PID still running |
| Stop, restart, normal close | Restore verified before process termination; restart asks again only if selected |
| Unexpected child exit | Restore on next idle poll; failure remains prominent |
| GUI force-kill, then reopen without core or from moved folder | Recovery finds current-user journal; exact applied state restores original |
| External edits during ownership or confirmation | No blind overwrite; stale decisions rejected; explicit restore/keep tested |
| Partial apply/restore failure, policy changes | Journal retained unless safely verified; force-stop prompt defaults to No |
| Corrupt journal or DPAPI user mismatch | No writes, no automatic deletion; actionable manual recovery |
| Concurrent app copies | Existing per-user mutex refuses second instance |
| Unsupported/managed policy | Refuse writes; no elevation or bypass |

After each case, verify Windows Settings and independent WinINet read-back. If restoring fails, retain the journal and use known original settings manually. Do not test on a production network first. Record evidence and fix failures before declaring Phase 2 accepted.

## API references

- [WinINet connection options and flags](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_optionw)
- [WinINet set/query guidance](https://learn.microsoft.com/en-us/windows/win32/wininet/setting-and-retrieving-internet-options)
- [WinINet notification option flags](https://learn.microsoft.com/en-us/windows/win32/wininet/option-flags)
- [Per-connection option list](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_option_listw)
- [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)
