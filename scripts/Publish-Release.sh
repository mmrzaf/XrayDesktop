#!/usr/bin/env bash
set -euo pipefail

# Run only in the tag-push release job, after the Windows acceptance checks in CI.
: "${GITHUB_REPOSITORY:?Missing GitHub repository}"
: "${GITHUB_REF_NAME:?Missing release tag}"
: "${EXPECTED_COMMIT:?Missing tested commit}"
: "${GH_TOKEN:?Missing GitHub release token}"
[[ ${GITHUB_EVENT_NAME:-} == push && ${GITHUB_REF_TYPE:-} == tag ]] || { echo 'Release publication requires a tag push.' >&2; exit 1; }
[[ $# == 1 ]] || { echo 'Usage: Publish-Release.sh <artifact-directory>' >&2; exit 1; }

tag=$GITHUB_REF_NAME
if [[ ! $tag =~ ^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
    echo 'Invalid release version tag.' >&2
    exit 1
fi
[[ $EXPECTED_COMMIT =~ ^[0-9a-f]{40}$ ]] || { echo 'Invalid tested commit.' >&2; exit 1; }

cd -- "$1"
archive="XrayDesktop-${tag#v}-win-x64.zip"
checksum="$archive.sha256"
[[ -s $archive && -f $checksum ]] || { echo 'Expected release files missing.' >&2; exit 1; }
# Compare the exact manifest, without letting it nominate arbitrary file paths.
[[ $(sha256sum -- "$archive") == "$(cat -- "$checksum")" ]] || { echo 'Release archive checksum mismatch.' >&2; exit 1; }

# Re-resolve the tag before publication; do not attach a build to a moved tag.
actual_commit=$(gh api "repos/$GITHUB_REPOSITORY/commits/$tag" --jq '.sha')
[[ $actual_commit == "$EXPECTED_COMMIT" ]] || { echo 'Tag no longer points to the tested commit.' >&2; exit 1; }

# Never overwrite an existing release or its assets on a rerun.
if gh release view "$tag" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
    echo "Release $tag already exists; refusing to overwrite it." >&2
    exit 1
fi

args=(--repo "$GITHUB_REPOSITORY" --verify-tag --title "Xray Desktop $tag" --generate-notes)
if [[ $tag == *-* ]]; then
    args+=(--prerelease --latest=false --notes 'Release candidate: automated Windows checks passed; interactive Windows proxy and private VLESS acceptance remain separate.')
fi
gh release create "$tag" "$archive" "$checksum" "${args[@]}"
