#!/usr/bin/env bash
# Installs a Windows agent release produced by scripts/build-agent.ps1 into the directory
# the server serves at /updates. Run this on the Ubuntu host after uploading the files.
set -euo pipefail

release_dir="${KIDTIME_RELEASE_DIR:-/opt/kidtime/releases}"
source_dir=""

usage() {
    cat <<'USAGE'
Usage: publish-agent-release.sh <source-directory> [--release-dir <path>]

The source directory is an upload of artifacts/releases from the Windows build machine.
It must contain latest.json, the agent ZIP that manifest names, and KidTimeSetup.exe.
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --release-dir) release_dir=${2:?--release-dir needs a value}; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        -*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
        *) source_dir=$1; shift ;;
    esac
done

[ -n "$source_dir" ] || { usage >&2; exit 2; }
[ -d "$source_dir" ] || { echo "Source directory not found: $source_dir" >&2; exit 1; }
[ -d "$release_dir" ] || { echo "Release directory not found: $release_dir. Run scripts/initialize-server.sh first." >&2; exit 1; }
[ -w "$release_dir" ] || { echo "Release directory is not writable: $release_dir" >&2; exit 1; }

manifest="$source_dir/latest.json"
[ -f "$manifest" ] || { echo "latest.json is missing from $source_dir" >&2; exit 1; }

manifest_text=$(tr -d '\r' < "$manifest")
field() { printf '%s' "$manifest_text" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n1; }
number() { printf '%s' "$manifest_text" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p" | head -n1; }

version=$(field version)
package_name=$(field fileName)
expected_hash=$(field sha256 | tr 'A-F' 'a-f')
expected_size=$(number sizeBytes)

[ -n "$version" ] && [ -n "$package_name" ] && [ -n "$expected_hash" ] && [ -n "$expected_size" ] \
    || { echo "latest.json does not contain version, fileName, sha256, and sizeBytes." >&2; exit 1; }
# The manifest names the package the server hands out; a path there would escape /updates.
case "$package_name" in */*|..*) echo "latest.json names an unsafe package file: $package_name" >&2; exit 1 ;; esac

package="$source_dir/$package_name"
[ -f "$package" ] || { echo "The agent package $package_name is missing from $source_dir" >&2; exit 1; }

# The server refuses a manifest whose package does not match byte for byte, so a
# truncated upload is worth catching here rather than on every controlled PC.
actual_size=$(stat -c%s "$package")
[ "$actual_size" = "$expected_size" ] || { echo "$package_name is $actual_size bytes but latest.json expects $expected_size." >&2; exit 1; }
actual_hash=$(sha256sum "$package" | cut -d' ' -f1)
[ "$actual_hash" = "$expected_hash" ] || { echo "$package_name does not match the SHA-256 in latest.json." >&2; exit 1; }

installer="$source_dir/KidTimeSetup.exe"
if [ ! -f "$installer" ]; then
    echo "Warning: KidTimeSetup.exe is not in $source_dir, so Add device cannot offer a download." >&2
    installer=""
fi

# Install the payloads before the manifest so the server never advertises a release
# whose package has not fully landed yet.
publish() {
    local from=$1 name=$2 staged="$release_dir/.$2.incoming"
    install -m 644 "$from" "$staged"
    mv -f "$staged" "$release_dir/$name"
}

publish "$package" "$package_name"
[ -z "$installer" ] || publish "$installer" "KidTimeSetup.exe"
publish "$manifest" "latest.json"

echo "Published agent $version to $release_dir."
echo "The server reads this directory on every request, so no container restart is needed."
