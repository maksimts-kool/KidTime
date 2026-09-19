#!/usr/bin/env bash
# Builds a Windows agent release from Linux and publishes it on the server, in one command.
#
# scripts/release-agent.ps1 does this from a Windows build machine. This is the same release
# driven from the Linux workstation: the build itself still has to happen on Windows - the
# SessionAgent and Setup projects are WPF and the installer is assembled with IExpress - so
# this script sends the working tree to a Windows machine over SSH, runs build-agent.ps1
# there, brings the release back, and publishes it here or on the server.
#
# Nothing is assumed to be checked out on either remote machine. The source travels as an
# archive of the working tree, the build step travels as a generated PowerShell script, and
# publish-agent-release.sh travels with the payload exactly as it does from Windows.
#
# The version bump is part of the release for the same reason it is there: the agent compares
# the published manifest with its own assembly version, so a release that reuses the current
# number reaches nobody.
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
version_file="$project_root/Directory.Build.props"
artifacts="$project_root/artifacts"
target_file="$artifacts/deploy-target.txt"
build_config_file="$artifacts/build-host.conf"
local_releases="$artifacts/releases"

# Defaults, before the remembered configuration, the environment, and the flags have had
# their say - in that order.
build_host=""
build_user=""
vm_domain=""
identity=""
ssh_options=()
build_dir='C:\kidtime-build'
configuration="Release"
bump="patch"
version=""
server=""
release_dir=""
skip_build=0
skip_publish=0
stop_vm=0

usage() {
    cat <<'USAGE'
Usage: release-agent.sh [options]

Builds the Windows agent on a Windows machine reached over SSH and publishes the result.

Build machine:
  --build-host user@host   SSH login of the Windows build machine.
  --vm domain              libvirt domain to start first; with no --build-host its address
                           is taken from the DHCP lease on every run.
  --build-user name        Windows account to log in as when the address comes from --vm.
  --identity path          SSH private key for the build machine.
  --ssh-option key=value   Extra ssh/scp option, repeatable (e.g. StrictHostKeyChecking=no).
  --build-dir path         Windows directory to build in (default C:\kidtime-build).
  --stop-vm                Shut the libvirt domain down again once the release is published.

Version:
  --bump major|minor|patch|none   How to move <Version> in Directory.Build.props (default patch).
  --version X.Y.Z                 An explicit version, which wins over --bump.
  --configuration Release|Debug   Build configuration (default Release).

Publishing:
  --server user@host       Ubuntu server to publish on. Without one the release is published
                           on this machine, which is where the server runs in a local setup.
  --release-dir path       Directory the server serves at /updates. Defaults to
                           KIDTIME_RELEASE_DIR from the environment or from .env.
  --skip-build             Publish what is already in artifacts/releases.
  --skip-publish           Build only, leaving the release in artifacts/releases.

The build machine and the server are remembered after the first run, so later releases are
just ./scripts/release-agent.sh
USAGE
}

fail() { printf 'error: %s\n' "$*" >&2; exit 1; }

step() {
    if [ -t 1 ]; then printf '\033[36m==> %s\033[0m\n' "$*"; else printf '==> %s\n' "$*"; fi
}

note() {
    if [ -t 1 ]; then printf '\033[32m%s\033[0m\n' "$*"; else printf '%s\n' "$*"; fi
}

# --- configuration -------------------------------------------------------------------

# Written by this script after a successful run; sourcing it is how a second release needs no
# flags at all. Flags and the environment are applied afterwards, so they still win.
# shellcheck disable=SC1090
[ -f "$build_config_file" ] && . "$build_config_file"

[ -n "${KIDTIME_BUILD_HOST:-}" ] && build_host=$KIDTIME_BUILD_HOST
[ -n "${KIDTIME_SERVER:-}" ] && server=$KIDTIME_SERVER
[ -n "${KIDTIME_RELEASE_DIR:-}" ] && release_dir=$KIDTIME_RELEASE_DIR
[ -z "$server" ] && [ -f "$target_file" ] && server=$(tr -d '\r' < "$target_file" | head -n1)

while [ $# -gt 0 ]; do
    case "$1" in
        --build-host) build_host=${2:?--build-host needs a value}; shift 2 ;;
        --build-user) build_user=${2:?--build-user needs a value}; shift 2 ;;
        --vm) vm_domain=${2:?--vm needs a value}; shift 2 ;;
        --identity) identity=${2:?--identity needs a value}; shift 2 ;;
        --ssh-option) ssh_options+=("${2:?--ssh-option needs a value}"); shift 2 ;;
        --build-dir) build_dir=${2:?--build-dir needs a value}; shift 2 ;;
        --stop-vm) stop_vm=1; shift ;;
        --bump) bump=${2:?--bump needs a value}; shift 2 ;;
        --version) version=${2:?--version needs a value}; shift 2 ;;
        --configuration) configuration=${2:?--configuration needs a value}; shift 2 ;;
        --server) server=${2:?--server needs a value}; shift 2 ;;
        --release-dir) release_dir=${2:?--release-dir needs a value}; shift 2 ;;
        --skip-build) skip_build=1; shift ;;
        --skip-publish) skip_publish=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) printf 'Unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
done

case "$bump" in major|minor|patch|none) ;; *) fail "--bump takes major, minor, patch, or none." ;; esac
case "$configuration" in Release|Debug) ;; *) fail "--configuration takes Release or Debug." ;; esac

# The local setup runs the server from this checkout, and .env is where that deployment says
# which directory it serves at /updates.
if [ -z "$release_dir" ] && [ -f "$project_root/.env" ]; then
    release_dir=$(sed -n 's/^KIDTIME_RELEASE_DIR=//p' "$project_root/.env" | head -n1 | tr -d '\r"')
fi
release_dir=${release_dir:-/opt/kidtime/releases}

# --- the Windows build machine -------------------------------------------------------

virsh_needs_sudo=0
virsh_run() {
    if [ "$virsh_needs_sudo" = 1 ]; then sudo virsh -c qemu:///system "$@"; else virsh -c qemu:///system "$@"; fi
}

ensure_vm_running() {
    command -v virsh >/dev/null 2>&1 || fail "virsh was not found, so --vm cannot start $vm_domain."
    virsh -c qemu:///system list >/dev/null 2>&1 || virsh_needs_sudo=1

    local state
    state=$(virsh_run domstate "$vm_domain" 2>/dev/null | head -n1 | tr -d '\r') || true
    case "$state" in
        running) return 0 ;;
        paused) step "Resuming $vm_domain"; virsh_run resume "$vm_domain" >/dev/null ;;
        "") fail "libvirt does not know a domain called $vm_domain." ;;
        *) step "Starting $vm_domain"; virsh_run start "$vm_domain" >/dev/null ;;
    esac
}

# libvirt is free to hand out a different address after the machine has been off for a while,
# so the lease is read on every run rather than remembered with the rest of the configuration.
resolve_vm_address() {
    local deadline=$((SECONDS + 180)) address=""
    while [ $SECONDS -lt $deadline ]; do
        address=$(virsh_run domifaddr "$vm_domain" --source lease 2>/dev/null |
            awk '/ipv4/ {print $4}' | cut -d/ -f1 | head -n1)
        [ -n "$address" ] && { printf '%s' "$address"; return 0; }
        sleep 5
    done
    fail "$vm_domain has no DHCP lease after three minutes."
}

ssh_flags() {
    local option
    [ -n "$identity" ] && printf '%s\0%s\0' -i "$identity"
    for option in "${ssh_options[@]+"${ssh_options[@]}"}"; do printf '%s\0%s\0' -o "$option"; done
    printf '%s\0%s\0' -o BatchMode=yes
}

build_ssh() {
    local -a flags=()
    mapfile -d '' -t flags < <(ssh_flags)
    ssh "${flags[@]}" -o ConnectTimeout=15 "$build_host" "$@"
}

build_scp() {
    local -a flags=()
    mapfile -d '' -t flags < <(ssh_flags)
    scp "${flags[@]}" -o ConnectTimeout=15 "$@"
}

# A Windows machine that has just been started answers ping long before it answers SSH.
wait_for_build_host() {
    local deadline=$((SECONDS + 300))
    while [ $SECONDS -lt $deadline ]; do
        if build_ssh "exit 0" >/dev/null 2>&1; then return 0; fi
        sleep 5
    done
    fail "$build_host did not answer SSH within five minutes."
}

# --- the version ---------------------------------------------------------------------

current_version() {
    sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$version_file" | head -n1 | tr -d '\r '
}

set_release_version() {
    local current next major minor patch
    current=$(current_version)
    [ -n "$current" ] || fail "Directory.Build.props does not contain a <Version> element."

    if [ -n "$version" ]; then
        next=$version
    elif [ "$bump" = "none" ]; then
        printf '%s' "$current"; return 0
    else
        [[ $current =~ ^([0-9]+)\.([0-9]+)(\.([0-9]+))?$ ]] || fail "The current version '$current' is not a version number."
        major=${BASH_REMATCH[1]}; minor=${BASH_REMATCH[2]}; patch=${BASH_REMATCH[4]:-0}
        case "$bump" in
            major) major=$((major + 1)); minor=0; patch=0 ;;
            minor) minor=$((minor + 1)); patch=0 ;;
            patch) patch=$((patch + 1)) ;;
        esac
        next="$major.$minor.$patch"
    fi

    [[ $next =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?(\.[0-9]+)?$ ]] || fail "'$next' is not a version number."
    if [ "$next" != "$current" ]; then
        sed -i "s|<Version>[^<]*</Version>|<Version>$next</Version>|" "$version_file"
        note "Version $current -> $next" >&2
    fi
    printf '%s' "$next"
}

manifest_version() {
    tr -d '\r' < "$local_releases/latest.json" |
        sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1
}

# --- build ---------------------------------------------------------------------------

# The working tree rather than a commit: the version bump above is not committed yet, and a
# release is often built from work in progress. git decides what belongs in it, so everything
# .gitignore already keeps out - artifacts, bin, obj, node_modules - stays out.
pack_source() {
    local archive=$1
    ( cd "$project_root" && git ls-files -z --cached --others --exclude-standard |
        while IFS= read -r -d '' file; do [ -f "$file" ] && printf '%s\0' "$file"; done |
        sort -zu | tar --null -T - -czf "$archive" )
}

write_remote_build_script() {
    local script=$1
    cat > "$script" <<REMOTE
# Generated by scripts/release-agent.sh. Unpacks the source it travelled with and runs the
# ordinary Windows build, so the Windows machine needs nothing checked out on it.
\$ErrorActionPreference = 'Stop'
\$buildDirectory = '$build_dir'
\$archive = Join-Path \$PSScriptRoot 'kidtime-release-src.tar.gz'
\$marker = Join-Path \$buildDirectory '.kidtime-build'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found on this machine.'
}
if (-not (Test-Path -LiteralPath \$archive)) { throw "The source archive \$archive did not arrive." }

# The directory is cleared on every release so a removed file cannot survive into the build.
# Only a directory this script created is ever cleared, so a mistyped --build-dir costs nothing.
if (Test-Path -LiteralPath \$buildDirectory) {
    if (-not (Test-Path -LiteralPath \$marker)) {
        throw "\$buildDirectory already exists and was not created by KidTime. Refusing to clear it."
    }
    Remove-Item -LiteralPath \$buildDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path \$buildDirectory -Force | Out-Null
Set-Content -LiteralPath \$marker -Value 'Created by scripts/release-agent.sh.' -Encoding ascii

# bsdtar is part of Windows itself from Windows 10 1803 on, so the payload needs no unpacker.
& tar.exe -xzf \$archive -C \$buildDirectory
if (\$LASTEXITCODE -ne 0) { throw 'The source archive could not be unpacked.' }
Remove-Item -LiteralPath \$archive -Force

& (Join-Path \$buildDirectory 'scripts\build-agent.ps1') -Configuration '$configuration'
REMOTE
}

build_release() {
    local staging archive remote_script
    staging=$(mktemp -d)
    trap 'rm -rf "$staging"' RETURN
    archive="$staging/kidtime-release-src.tar.gz"
    remote_script="$staging/kidtime-release-build.ps1"

    step "Packing the working tree"
    pack_source "$archive"
    write_remote_build_script "$remote_script"

    step "Sending the source to $build_host"
    # Both land in the login account's home directory, which is the one path on a Windows
    # machine this script can name without knowing anything about it.
    build_scp "$archive" "$remote_script" "$build_host:"

    step "Building the agent, update package, and setup executable on $build_host"
    # PowerShell is the login shell there, and -File with no arguments to quote is what keeps
    # this command free of the escaping that a build command line would otherwise need.
    build_ssh "powershell -NoProfile -ExecutionPolicy Bypass -File .\\kidtime-release-build.ps1"

    step "Fetching the release"
    rm -rf "$local_releases"
    mkdir -p "$artifacts"
    build_scp -r "$build_host:${build_dir//\\//}/artifacts/releases" "$artifacts/"
}

# --- publishing ----------------------------------------------------------------------

publish_here() {
    step "Publishing agent $release_version in $release_dir"
    bash "$project_root/scripts/publish-agent-release.sh" "$local_releases" --release-dir "$release_dir"
}

publish_on_server() {
    local staging="/tmp/kidtime-release"
    step "Preparing $server"
    ssh "$server" "rm -rf '$staging' && mkdir -p '$staging'"

    step "Uploading agent $release_version"
    scp "$local_releases/latest.json" \
        "$local_releases/kidtime-agent-$release_version.zip" \
        "$local_releases/KidTimeSetup.exe" \
        "$project_root/scripts/publish-agent-release.sh" \
        "$server:$staging/"

    step "Publishing agent $release_version"
    # publish-agent-release.sh does the verifying: it refuses a package whose size or SHA-256
    # does not match the manifest, and writes latest.json last so the server never advertises
    # a release whose package has not fully landed. The staging directory is left in place if
    # it fails, so the upload can be inspected.
    ssh "$server" "bash '$staging/publish-agent-release.sh' '$staging' --release-dir '$release_dir' && rm -rf '$staging'"
}

remember_configuration() {
    mkdir -p "$artifacts"
    {
        printf '# Written by scripts/release-agent.sh; flags and the environment still win.\n'
        printf 'build_host=%q\n' "$build_host_setting"
        printf 'build_user=%q\n' "$build_user"
        printf 'vm_domain=%q\n' "$vm_domain"
        printf 'identity=%q\n' "$identity"
        printf 'build_dir=%q\n' "$build_dir"
        printf 'ssh_options=('
        # An empty array still has to be written as one: printf with no arguments would
        # otherwise leave a single empty option behind, which ssh reads as -o ''.
        if [ ${#ssh_options[@]} -gt 0 ]; then printf '%q ' "${ssh_options[@]}"; fi
        printf ')\n'
    } > "$build_config_file"
    [ -n "$server" ] && printf '%s\n' "$server" > "$target_file"
    return 0
}

# --- the release ---------------------------------------------------------------------

# Remembered as it was given: an address resolved from a DHCP lease is true for one run only.
build_host_setting=$build_host

if [ "$skip_build" = 0 ]; then
    [ -n "$vm_domain" ] || [ -n "$build_host" ] ||
        fail "No Windows build machine was given. Run it once with --build-host user@host (or --vm domain --build-user name); it is remembered after that."
    command -v git >/dev/null 2>&1 || fail "git was not found, and the source archive is built from it."

    if [ -n "$vm_domain" ]; then
        ensure_vm_running
        if [ -z "$build_host" ]; then
            [ -n "$build_user" ] || fail "--vm needs --build-user to know which Windows account to log in as."
            build_host="$build_user@$(resolve_vm_address)"
        fi
    fi

    step "Waiting for $build_host"
    wait_for_build_host

    previous_version=$(current_version)
    release_version=$(set_release_version)
    build_release

    [ -f "$local_releases/latest.json" ] || fail "The build did not produce artifacts/releases/latest.json."
    built_version=$(manifest_version)
    # A build that produced a different number means the machine built something other than
    # what was sent - stale sources, or a bump that never reached it. Publishing that would
    # advertise a release nobody can match.
    [ "$built_version" = "$release_version" ] ||
        fail "The build produced agent $built_version but this release is $release_version."
else
    [ -f "$local_releases/latest.json" ] || fail "There is no artifacts/releases/latest.json to publish."
    release_version=$(manifest_version)
    [ -n "$release_version" ] || fail "artifacts/releases/latest.json does not name a version."
    note "Publishing the agent $release_version already in artifacts/releases."
fi

for file in "latest.json" "kidtime-agent-$release_version.zip" "KidTimeSetup.exe"; do
    [ -f "$local_releases/$file" ] || fail "The release is missing $file."
done

if [ "$skip_publish" = 1 ]; then
    note "Agent $release_version is in artifacts/releases. Publishing was skipped."
elif [ -n "$server" ]; then
    publish_on_server
else
    publish_here
fi

remember_configuration

if [ "$stop_vm" = 1 ] && [ -n "$vm_domain" ]; then
    step "Shutting $vm_domain down"
    virsh_run shutdown "$vm_domain" >/dev/null
fi

printf '\n'
if [ "$skip_publish" = 1 ]; then
    note "Nothing was published."
else
    note "Agent $release_version is published in $release_dir${server:+ on $server}."
    printf 'Controlled PCs install it on their next update check; nothing there has to be restarted.\n'
fi
[ "$release_version" != "${previous_version:-$release_version}" ] &&
    printf 'Commit the version bump in Directory.Build.props together with the change it ships.\n'
exit 0
