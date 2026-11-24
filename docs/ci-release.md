# CI, release publication and first-run fixes

## First Windows CI evidence

The supplied run on `656422d144a6b6e83bf66b7f0e214d201f0d2be2` (2026-11-24) verified the pinned SDK and Xray archive, restored and compiled both projects with **0 warnings and 0 errors**, and passed 76 of 77 mandatory checks. Native WinINet read-only passed. The hidden UI check was not requested. Packaging did not execute because the check harness returned exit code 1.

Two failures were reported:

- `Direct HTTPS reaches origin and rejects untrusted TLS: A task was canceled.` The fixture waited for the client before observing the server handshake, and a server-side failure could leave the transport open until cancellation. The fixture now imports a temporary user-key-set PFX for Schannel, runs a positive handshake pinned to the generated certificate, and independently serves and disposes the connection for the negative test. The production TLS validation is unchanged. A timeout is still a failure.
- `real Xray integration: Access to the path 'xray.exe' is denied.` The original fixture copied the binary into a temporary directory and deleted that directory in `finally`; Windows image/AV handles or file attributes can prevent deletion, and cleanup could mask an earlier failure. The new fixture runs the supplied verified executable in its existing directory, only cleans its own generated config/data, retains the killed process handle, logs integration stages, and preserves exceptions if directory cleanup also fails. No unrelated processes are killed. The log did not include a stack trace, so the original access-denied source is not conclusively established.

Both fixes need a Windows CI rerun. Full exception stacks from synthetic tests are now printed on failure. No tests were skipped or relaxed to make CI pass.

## Trigger matrix

| Event | Windows build + checks | Release guard tests | GitHub Release |
|---|---|---|---|
| Push to `main` or `develop` | Yes | Yes | No |
| PR targeting `main` or `develop` | Yes | Yes | No |
| Manual dispatch | Yes | Yes | No |
| Push `v*` tag | Yes, tag/version must match | Yes | Only after both jobs succeed |
| Other branch push | No | No | No |

`Directory.Build.props` is the single application/test version source. Tag format is `v<major>.<minor>.<patch>` or `v<major>.<minor>.<patch>-<prerelease>` with no build metadata. The checked-out project version must match the tag exactly. Do not move or reuse published release tags. Protect version tags with repository rulesets where possible.

## Publish a candidate

From an up-to-date `main` with both CI jobs passing, using Git Bash or any Git terminal:

```sh
git switch main
git pull --ff-only origin main
git tag -a v0.1.0 -m "Release 0.1.0"
git push origin v0.1.0
```

The tag runs the full Windows pipeline again. Publication downloads the actual tested artifact, verifies its SHA-256 manifest again, resolves the exact tag ref (including annotated tags) to the tested commit, refuses existing releases, then creates a GitHub Release with generated notes and these two files:

- `XrayDesktop-0.1.0-win-x64.zip`
- `XrayDesktop-0.1.0-win-x64.zip.sha256`

Tags with a prerelease suffix are marked as prereleases and not latest; `v0.1.0` has no suffix, so it publishes as a regular release. A stable release still requires first passing the manual acceptance checklist. Publication of a stable tag is an explicit maintainer approval; CI cannot verify interactive/manual checks on its own. No Xray binary or private configuration is published. No PAT is required: the publication job uses the repository's `GITHUB_TOKEN` with `contents: write`, subject to repository/organization rules. The other jobs have read-only contents permissions. If a release already exists, review it rather than overwriting its assets or moving the tag.

## Release guard checks

```sh
bash scripts/Test-Release.sh
```

The shell fixture uses a mocked `gh` executable and synthetic assets. It verifies prerelease and stable annotated-tag paths, moved-tag rejection, existing-release refusal, checksum mismatch, missing checksum, branch/manual event rejection and invalid tag rejection. These checks run on Ubuntu independently of the Windows build. They do not make any real GitHub requests or establish Windows acceptance.
