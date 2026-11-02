#!/usr/bin/env bash
# Publishes a new version: builds macOS + Windows, tags vX.Y.Z, uploads both to a GitHub release.
# Installed copies of ChatSpace check the latest release every 4 hours and update themselves.
#
# usage: scripts/release.sh 0.2.0 ["what changed"]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REPO="leduyphuc1702/chat-space"
VERSION="${1:?usage: scripts/release.sh X.Y.Z [notes]}"
NOTES="${2:-}"
cd "$ROOT"

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "version must look like 1.2.3" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "commit or stash your changes first" >&2; git status --short >&2; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = main ] || { echo "release from main" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/v$VERSION" >/dev/null && { echo "v$VERSION already exists" >&2; exit 1; }
[ -d win/ChatSpace/Assets/Native ] && [ -d win/ChatSpace/Assets/Fonts ] || {
  echo "Windows assets missing; see .gitignore for how to regenerate them" >&2; exit 1; }

printf '%s\n' "$VERSION" > VERSION
trap 'git checkout -- VERSION 2>/dev/null || true' ERR

./scripts/build-app.sh
./win/build.sh

OUT="$ROOT/dist/release"
rm -rf "$OUT" && mkdir -p "$OUT"
ditto -c -k --keepParent dist/ChatSpace.app "$OUT/ChatSpace-macos.zip"
cp dist/ChatSpace-win-x64.exe "$OUT/ChatSpace-win-x64.exe"

trap - ERR
git commit -qm "Release v$VERSION" VERSION
git tag "v$VERSION"
git push -q origin main "v$VERSION"

args=(--title "ChatSpace $VERSION")
if [ -n "$NOTES" ]; then args+=(--notes "$NOTES"); else args+=(--generate-notes); fi
gh release create "v$VERSION" "$OUT/ChatSpace-macos.zip" "$OUT/ChatSpace-win-x64.exe" --repo "$REPO" "${args[@]}"
echo "==> https://github.com/$REPO/releases/tag/v$VERSION"
