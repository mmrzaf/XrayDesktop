# Phase 1 — Core control acceptance record

Source preview: `0.1.0-preview.1`. Baseline: Xray v26.11.9. Phase 0 Windows build gate remains open; proceeding with source work does not waive that gate.

## Implemented

- Adjacent executable discovery and native version check; no automatic download.
- Raw-byte profile reference/copy, managed IDs, explicit editing and Save As; JSONC retained unless intentionally edited. Strict JSON formatting refuses comments/trailing commas. UTF-8 editor supports BOM; other encodings may be imported and validated as raw bytes but cannot be edited in the editor.
- Bounded settings/config reads, atomic replacement, corrupt settings warning; per-user global mutex.
- Best-effort loopback HTTP/SOCKS detection, explicit selection, manual loopback override, port collision warning and native IPv4/IPv6 TCP owner lookup. Ownership is not protocol readiness or connectivity.
- Snapshot hashing, native validation, process argument list, child working directory and adjacent asset directory. Validation and start use the same snapshot; no source mutation.
- Serialized GUI operations, cancellation, start/stop/restart, process exit polling, bounded output drain and private kill-on-close job. Closing the job is termination, not a graceful Xray shutdown.
- Local raw logs are bounded, with explicit confirmation before copying. No OS proxy writes or public probes.

## Known limitations / decisions

- Windows build, native job/owner-table behavior, visual layout and target-core integration remain unexecuted in the source-authoring environment. No compiled EXE is claimed.
- Job assignment follows Process.Start rather than suspended creation; a narrow crash window exists. A live child that cannot be assigned is terminated; a child that already exited can be observed. Job assignment must be tested under CI runner job nesting and real Windows.
- Runtime snapshot hash checks detect changes before start, not hostile same-user modifications after verification. Files inherit the portable folder ACL; no new ACL or encryption guarantee. Use a private writable directory. On abnormal shutdown or uncertain cleanup, snapshots may remain and are not auto-deleted. Settings do not keep a backup yet; corrupt files are retained until explicit replacement.
- One endpoint is verified per session. Manual endpoint override is transient, not persisted; selected profile persists. Working directory is inferred from original file or Save As destination, not editable separately. Profile names derive from file name; managed copy file remains after reference removal. Settings and editor UX are still development previews.
- Raw local core output is not safe to share; no sanitized report export until Phase 3. Core-configured file logs are not captured. Version detection is not a binary authenticity check. No automatic core start, no proxy recovery logic yet.
- Explicitly recognized TUN inbound is refused at start. Unknown config features are not sandboxed; use trusted configs only. Non-loopback exposure is warned, not rewritten. HTTP/SOCKS capability testing is deferred.
- Cancellation is available for main operations; editor validation is bounded but does not have a separate cancel button. No auto-reconnect. A saved profile cannot be edited while running through this UI.

## Verification matrix

| SDD gate | Added checks / expected manual verification | Evidence |
| --- | --- | --- |
| P0 | Release compile, package, extracted EXE checks | Pending Windows CI |
| A01 | Missing core check; native version parser; real version command, wrong-architecture/timeouts manual | Source checks added, execution pending |
| A02 | Advisory malformed JSON check; actual native reject/missing geodata manual | Pending |
| A03 | Byte-preserving profile copy, settings, snapshot tests; editor/relative path manual | Pending |
| A04 | JSONC/dynamic port/auth/unsafe transport tests; IPv4/IPv6 owner lookup; actual core integration | Pending |
| A05 | Native ownership check against wrong PID; port collision/unrelated Xray manual | Pending |
| A06 | Synthetic owned child exit/drain and stop; optional actual Xray lifecycle; rapid restart/cancel and crash manual | Pending |

The default build includes 20 mandatory console checks and five published EXE smoke checks. The real Xray test is separate and skipped unless `XRAY_TEST_CORE` points to a trusted target binary. No private configuration is required. The optional test validates, starts, verifies a dynamic HTTP listener and stops the actual core; it does not establish public connectivity.

## Acceptance procedure (Windows)

1. Run `scripts/Build.ps1` in Windows CI; retain logs/artifacts. Resolve failures instead of bypassing checks.
2. Supply v26.11.9 via `XRAY_TEST_CORE` and rerun. Record core version and OS build.
3. Extract ZIP in a private path containing spaces and Unicode, launch without development tools. Test missing/wrong core, corrupt settings, valid/invalid JSONC, edit/cancel/Save As, external changes, missing assets and relative paths.
4. Use a trusted synthetic config with dynamic loopback ports. Validate/start, check PID ownership, stop/restart; test existing listener collisions, unrelated Xray process, rapid operations, cancellation and unexpected core exit. Compare Windows proxy settings before/after (must be unchanged).
5. Force-kill GUI in isolated test account; verify assigned child terminates and document any leftover snapshots. Test GUI responsiveness during output flood and DPI sizing. Record actual outcomes; do not label stable until gates pass.

No proxy changes should be made in these tests. Phase 2 is not part of this deliverable.
