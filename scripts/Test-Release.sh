#!/usr/bin/env bash
set -euo pipefail

# Local regression checks for the release script; no network or real credentials.
script="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/Publish-Release.sh"
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
mkdir "$work/bin" "$work/assets"
export PATH="$work/bin:$PATH" GH_TOKEN=fixture-only GITHUB_REPOSITORY=fixture/repo
export GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=tag
export EXPECTED_COMMIT=1111111111111111111111111111111111111111
export TAG_OBJECT=2222222222222222222222222222222222222222
export TEST_LOG="$work/gh.log" TEST_MODE=lightweight
cat > "$work/bin/gh" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
    'api repos/fixture/repo/git/ref/tags/'*)
        case "$TEST_MODE" in
            moved) echo 'commit 3333333333333333333333333333333333333333' ;;
            annotated) echo "tag $TAG_OBJECT" ;;
            *) echo "commit $EXPECTED_COMMIT" ;;
        esac ;;
    'api repos/fixture/repo/git/tags/'*) echo "commit $EXPECTED_COMMIT" ;;
    'release view') [[ $TEST_MODE == existing ]] ;;
    'release create') printf '%s\n' "$@" > "$TEST_LOG" ;;
    *) echo 'Unexpected fixture command.' >&2; exit 2 ;;
esac
MOCK
chmod +x "$work/bin/gh"

prepare() {
    export GITHUB_REF_NAME=$1
    rm -f -- "$TEST_LOG"
    local archive="XrayDesktop-${GITHUB_REF_NAME#v}-win-x64.zip"
    (cd "$work/assets"; printf 'synthetic release bytes\n' > "$archive"; sha256sum -- "$archive" > "$archive.sha256")
}
expect_refusal() {
    if bash "$script" "$work/assets"; then echo "Unexpected release success: $1" >&2; exit 1; fi
    [[ ! -e $TEST_LOG ]] || { echo "Release command called on refusal: $1" >&2; exit 1; }
    echo "PASS: $1"
}
prepare v1.0.0-rc.1
bash "$script" "$work/assets"
grep -Fxq -- '--prerelease' "$TEST_LOG"
grep -Fxq -- 'XrayDesktop-1.0.0-rc.1-win-x64.zip' "$TEST_LOG"
grep -Fxq -- 'XrayDesktop-1.0.0-rc.1-win-x64.zip.sha256' "$TEST_LOG"
echo 'PASS: candidate publication includes both assets and prerelease flag'

prepare v1.0.0
export TEST_MODE=annotated
bash "$script" "$work/assets"
if grep -Fxq -- '--prerelease' "$TEST_LOG"; then echo 'Stable tag marked prerelease.' >&2; exit 1; fi
echo 'PASS: annotated stable tag resolves to tested commit'

prepare v1.0.0-rc.1
export TEST_MODE=moved
expect_refusal 'moved tag'
export TEST_MODE=existing
expect_refusal 'existing release'
export TEST_MODE=lightweight
printf 'bad\n' >> "$work/assets/XrayDesktop-1.0.0-rc.1-win-x64.zip"
expect_refusal 'checksum mismatch'
prepare v1.0.0-rc.1
rm -- "$work/assets/XrayDesktop-1.0.0-rc.1-win-x64.zip.sha256"
expect_refusal 'missing checksum'
prepare v1.0.0-rc.1
export GITHUB_REF_TYPE=branch
expect_refusal 'branch event'
export GITHUB_REF_TYPE=tag GITHUB_EVENT_NAME=workflow_dispatch
expect_refusal 'manual event'
export GITHUB_EVENT_NAME=push GITHUB_REF_NAME=version-one
expect_refusal 'invalid tag'
