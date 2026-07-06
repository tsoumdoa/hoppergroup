#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  ./scripts/release-yak.sh VERSION [--push-test] [--push-public] [--allow-dirty]

Examples:
  ./scripts/release-yak.sh 0.2.0
  ./scripts/release-yak.sh 0.2.0-beta.1 --push-test
USAGE
}

log() {
  printf '[release-yak] %s\n' "$*"
}

fail() {
  printf '[release-yak] error: %s\n' "$*" >&2
  exit 1
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

version=""
push_target=""
allow_dirty=0

while (($#)); do
  case "$1" in
    --push-test)
      [[ -z "$push_target" ]] || fail "choose only one publish target"
      push_target="test"
      ;;
    --push-public)
      [[ -z "$push_target" ]] || fail "choose only one publish target"
      push_target="public"
      ;;
    --allow-dirty)
      allow_dirty=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    -*)
      fail "unknown option: $1"
      ;;
    *)
      [[ -z "$version" ]] || fail "only one VERSION argument is allowed"
      version="$1"
      ;;
  esac
  shift
done

[[ -n "$version" ]] || { usage; fail "VERSION is required"; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]] || fail "VERSION must be SemVer like 0.2.0 or 0.2.0-beta.1"

if [[ -n "${YAK_BIN:-}" ]]; then
  yak_bin="$YAK_BIN"
elif command -v yak >/dev/null 2>&1; then
  yak_bin="$(command -v yak)"
elif [[ -x "/Applications/Rhino 8.app/Contents/Resources/bin/yak" ]]; then
  yak_bin="/Applications/Rhino 8.app/Contents/Resources/bin/yak"
else
  fail "Yak CLI not found. Set YAK_BIN, add yak to PATH, or install Rhino 8."
fi

[[ -x "$yak_bin" ]] || fail "Yak CLI is not executable: $yak_bin"

if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  if git rev-parse --verify HEAD >/dev/null 2>&1; then
    if [[ "$allow_dirty" -eq 0 && -n "$(git status --porcelain)" ]]; then
      fail "working tree is dirty; commit/stash changes or pass --allow-dirty"
    fi
  else
    log "Git repository has no commits yet; skipping clean working tree check."
  fi
fi

csproj="HopperGroup.csproj"
manifest="yak/manifest.yml"
readme="README.md"
icon="yak/icon.png"

[[ -f "$csproj" ]] || fail "missing $csproj"
[[ -f "$manifest" ]] || fail "missing $manifest"
[[ -f "$readme" ]] || fail "missing $readme"
[[ -f "$icon" ]] || fail "missing $icon"

csproj_version_count="$(grep -Ec '<Version>[^<]+</Version>' "$csproj" || true)"
manifest_version_count="$(grep -Ec '^version:[[:space:]]*[^[:space:]]+' "$manifest" || true)"
[[ "$csproj_version_count" == "1" ]] || fail "expected exactly one <Version> in $csproj, found $csproj_version_count"
[[ "$manifest_version_count" == "1" ]] || fail "expected exactly one top-level version field in $manifest, found $manifest_version_count"

backup_dir="$(mktemp -d)"
restore_on_error=0

cleanup() {
  status=$?
  if [[ "$status" -ne 0 && "$restore_on_error" -eq 1 ]]; then
    cp "$backup_dir/HopperGroup.csproj" "$csproj"
    cp "$backup_dir/manifest.yml" "$manifest"
    log "Restored $csproj and $manifest after failure."
  fi
  rm -rf "$backup_dir"
  exit "$status"
}
trap cleanup EXIT

cp "$csproj" "$backup_dir/HopperGroup.csproj"
cp "$manifest" "$backup_dir/manifest.yml"
restore_on_error=1

log "Updating source versions to $version."
perl -0pi -e "s#<Version>[^<]+</Version>#<Version>$version</Version>#" "$csproj"
perl -0pi -e "s#^version:[[:space:]]*[^[:space:]]+#version: $version#m" "$manifest"

log "Building Release targets."
dotnet build "$csproj" -c Release

artifact_dir="artifacts/yak"
stage_dir="$artifact_dir/stage"
rm -rf "$stage_dir"
mkdir -p "$stage_dir/misc" "$stage_dir/net48" "$stage_dir/net7.0" "$stage_dir/net7.0-windows" "$artifact_dir"

log "Staging Yak package contents."
cp "$manifest" "$stage_dir/manifest.yml"
cp "$icon" "$stage_dir/icon.png"
cp "$readme" "$stage_dir/misc/README.md"
cp "bin/Release/net48/HopperGroup.gha" "$stage_dir/net48/HopperGroup.gha"
cp "bin/Release/net7.0/HopperGroup.gha" "$stage_dir/net7.0/HopperGroup.gha"
cp "bin/Release/net7.0-windows/HopperGroup.gha" "$stage_dir/net7.0-windows/HopperGroup.gha"

rm -f "$artifact_dir"/hoppergroup-"$version"-*.yak

log "Building Yak package."
(
  cd "$stage_dir"
  "$yak_bin" build --platform any --version "$version"
)

shopt -s nullglob
built_packages=("$stage_dir"/hoppergroup-"$version"-*.yak)
shopt -u nullglob
[[ "${#built_packages[@]}" -eq 1 ]] || fail "expected one built Yak package, found ${#built_packages[@]}"

artifact="$artifact_dir/$(basename "${built_packages[0]}")"
mv "${built_packages[0]}" "$artifact"

log "Created $artifact"
log "Package contents:"
unzip -l "$artifact"

case "$push_target" in
  test)
    log "Pushing to Yak test server."
    "$yak_bin" push --source https://test.yak.rhino3d.com "$artifact"
    "$yak_bin" search --source https://test.yak.rhino3d.com --all --prerelease hoppergroup
    ;;
  public)
    log "Pushing to public Yak server."
    "$yak_bin" push "$artifact"
    ;;
esac

restore_on_error=0
