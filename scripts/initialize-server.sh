#!/usr/bin/env bash
# Creates the KidTime server secrets, HTTPS certificate, and host directories on Ubuntu.
# Run once on the Docker host before the stack is deployed with Portainer or Compose.
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)

admin_email="parent@kidtime.local"
admin_password=""
agent_url=""
cert_dir="/opt/kidtime/certs"
release_dir="/opt/kidtime/releases"
env_file="$project_root/.env"
extra_hosts=()
bind_address="0.0.0.0"
server_port="5081"
web_port="3000"
base_path=""

usage() {
    cat <<'USAGE'
Usage: initialize-server.sh [options]

  --admin-email <address>     First parent account (default parent@kidtime.local)
  --admin-password <secret>   Parent password (default: generated)
  --agent-url <url>           HTTPS URL the controlled PCs use (default https://<host ip>:5081)
  --host <name-or-ip>         Extra certificate subject alternative name; repeatable
  --cert-dir <path>           Host directory bind-mounted at /https (default /opt/kidtime/certs)
  --release-dir <path>        Host directory bind-mounted at /updates (default /opt/kidtime/releases)
  --env-file <path>           Where to write the generated secrets (default <repo>/.env)

Behind a reverse proxy on the same host:
  --base-path <prefix>        Path the panel is published under, e.g. /kidtime
  --bind <address>            Address the container ports publish on (default 0.0.0.0)
  --server-port <port>        Host port for the agent API (default 5081)
  --web-port <port>           Host port for the panel (default 3000)
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --admin-email) admin_email=${2:?--admin-email needs a value}; shift 2 ;;
        --admin-password) admin_password=${2:?--admin-password needs a value}; shift 2 ;;
        --agent-url) agent_url=${2:?--agent-url needs a value}; shift 2 ;;
        --host) extra_hosts+=("${2:?--host needs a value}"); shift 2 ;;
        --cert-dir) cert_dir=${2:?--cert-dir needs a value}; shift 2 ;;
        --release-dir) release_dir=${2:?--release-dir needs a value}; shift 2 ;;
        --env-file) env_file=${2:?--env-file needs a value}; shift 2 ;;
        --base-path) base_path=${2:?--base-path needs a value}; shift 2 ;;
        --bind) bind_address=${2:?--bind needs a value}; shift 2 ;;
        --server-port) server_port=${2:?--server-port needs a value}; shift 2 ;;
        --web-port) web_port=${2:?--web-port needs a value}; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

# A base path is concatenated with request paths on both sides, so normalise it once.
if [ -n "$base_path" ]; then
    base_path="/${base_path#/}"
    base_path="${base_path%/}"
fi

command -v openssl >/dev/null || { echo "openssl is required. Install it with: sudo apt install openssl" >&2; exit 1; }

if [ -e "$env_file" ]; then
    echo "KidTime server configuration already exists at $env_file"
    echo "Delete it deliberately if the secrets really have to be regenerated."
    exit 0
fi

new_secret() { openssl rand -base64 "${1:-36}" | tr '+/' '-_' | tr -d '=\n'; }

# pipefail is on, so every detection fallback has to be allowed to fail quietly.
host_name=$(hostname -f 2>/dev/null || hostname 2>/dev/null || true)
[ -n "$host_name" ] || host_name="kidtime"
host_ip=$(ip -4 route get 1.1.1.1 2>/dev/null | awk '{for (i = 1; i <= NF; i++) if ($i == "src") { print $(i + 1); exit }}' || true)
[ -n "$host_ip" ] || host_ip=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
[ -n "$host_ip" ] || host_ip="127.0.0.1"
[ -n "$agent_url" ] || agent_url="https://$host_ip:5081"

subject_alt_names="DNS:$host_name,DNS:localhost,IP:$host_ip,IP:127.0.0.1"
for entry in ${extra_hosts+"${extra_hosts[@]}"}; do
    if [[ $entry =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
        subject_alt_names="$subject_alt_names,IP:$entry"
    else
        subject_alt_names="$subject_alt_names,DNS:$entry"
    fi
done

# The directories are bind-mounted into containers that run as a non-root application
# user, so the server has to be able to read them without owning them.
if ! install -d -m 755 "$cert_dir" "$release_dir"; then
    echo "Could not create $cert_dir and $release_dir. Re-run with sudo." >&2
    exit 1
fi
mkdir -p -- "$(dirname -- "$env_file")"

[ -n "$admin_password" ] || admin_password=$(new_secret 18)
postgres_password=$(new_secret 36)
jwt_key=$(new_secret 48)
certificate_password=$(new_secret 24)

work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT

# Key generation writes progress dots to stderr, so the log is shown only on failure.
quietly() {
    if ! "$@" 2>"$work_dir/openssl.log"; then
        cat "$work_dir/openssl.log" >&2
        exit 1
    fi
}

quietly openssl req -x509 -newkey rsa:2048 -sha256 -days 3650 -noenc \
    -keyout "$work_dir/kidtime.key" -out "$work_dir/kidtime.crt" \
    -subj "/CN=$host_name" \
    -addext "subjectAltName=$subject_alt_names" \
    -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
    -addext "extendedKeyUsage=serverAuth"

quietly openssl pkcs12 -export -name kidtime \
    -inkey "$work_dir/kidtime.key" -in "$work_dir/kidtime.crt" \
    -out "$work_dir/kidtime.pfx" -passout "pass:$certificate_password"

# The agent pins this exact SHA-256 of the DER certificate, so it must be recorded
# before the private material is discarded.
pin=$(openssl x509 -in "$work_dir/kidtime.crt" -outform DER | sha256sum | cut -d' ' -f1 | tr 'a-f' 'A-F')

install -m 644 "$work_dir/kidtime.pfx" "$cert_dir/kidtime.pfx"
printf '%s' "$pin" > "$work_dir/kidtime.sha256"
install -m 644 "$work_dir/kidtime.sha256" "$cert_dir/kidtime.sha256"

umask 177
cat > "$env_file" <<ENVIRONMENT
POSTGRES_PASSWORD=$postgres_password
KIDTIME_JWT_KEY=$jwt_key
KIDTIME_ADMIN_EMAIL=$admin_email
KIDTIME_ADMIN_PASSWORD=$admin_password
KIDTIME_CERT_PASSWORD=$certificate_password
KIDTIME_AGENT_URL=$agent_url
KIDTIME_CERT_DIR=$cert_dir
KIDTIME_RELEASE_DIR=$release_dir
KIDTIME_BIND=$bind_address
KIDTIME_SERVER_PORT=$server_port
KIDTIME_WEB_PORT=$web_port
KIDTIME_BASE_PATH=$base_path
ENVIRONMENT
umask 022

# A sudo-invoked run should leave the checkout usable by the account that owns it.
if [ -n "${SUDO_UID:-}" ] && [ -n "${SUDO_GID:-}" ]; then
    chown "$SUDO_UID:$SUDO_GID" "$env_file"
    chown -R "$SUDO_UID:$SUDO_GID" "$release_dir"
fi

cat <<SUMMARY
KidTime server configuration created.

  Parent email        $admin_email
  Parent password     stored only in $env_file
  Certificate pin     $pin
  Certificate names   $subject_alt_names
  Agent enrollment    $agent_url
  Certificate mount   $cert_dir -> /https
  Release mount       $release_dir -> /updates
  Published ports     $bind_address:$server_port (agent API), $bind_address:$web_port (panel)
  Panel base path     ${base_path:-/ (domain root)}

Next steps
  1. Deploy the stack. With the Compose CLI: docker compose up --build -d
     With Portainer: create the stack from this repository and paste the
     contents of $env_file into the stack's environment variables.
  2. Build the Windows agent on a Windows machine (scripts/build-agent.ps1),
     upload artifacts/releases to this host, and run
     scripts/publish-agent-release.sh <uploaded-directory>.
SUMMARY
