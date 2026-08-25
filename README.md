# KidTime

KidTime is a self-hosted Windows screen-time and application-control system. The server and web panel run as Docker containers on an Ubuntu host, typically managed with Portainer; a privileged `ControlService` and interactive `SessionAgent` run on the controlled Windows 11 PC.

KidTime deliberately does **not** collect or filter websites, DNS queries, browser history, searches, messages, keystrokes, screenshots, camera/microphone data, or network traffic.

## Architecture

```text
Parent browser ──HTTP──> Next.js web proxy ──JWT──> ASP.NET Core API
                                                   │
                                                   ├── PostgreSQL
                                                   └── HTTPS + SignalR
                                                          │
Controlled PC                                      ControlService (LocalSystem)
                                                   ├── SQLite rules/usage queue
                                                   ├── process discovery/enforcement
                                                   ├── final-warning timing + Windows sign-out
                                                   └── secured named pipe
                                                          │
                                                   SessionAgent (logged-in user)
                                                   ├── foreground app + idle detection
                                                   ├── native Windows tray status UI
                                                   └── native Windows notifications
```

- REST handles enrollment, heartbeats, rules, usage batches, discovery, and administration.
- SignalR wakes the agent for immediate commands. Periodic sync remains the fallback and SignalR is never required for enforcement.
- Parent JWTs live only in a secure, HTTP-only, same-site cookie managed by Next.js.
- Each device receives a random credential after redeeming a single-use enrollment token. Only a SHA-256 digest is stored on the server.
- The service caches the last valid rule snapshot in SQLite and never changes to “allow everything” merely because the server is unavailable.
- The app catalog exposes parent-manageable user apps rather than Windows services, helpers, update components, or KidTime itself.

See [Architecture and security](docs/architecture-security.md) for rule precedence, time accounting, IPC, identity, and first-version anti-tamper boundaries.

## Prerequisites

### Server (Ubuntu)

- Ubuntu 22.04 or newer with Docker Engine and the Compose plugin
- Portainer CE, if the stack is managed through a UI rather than the Compose CLI
- `openssl` and `git`, both present on a standard Ubuntu install
- TCP 5081 reachable from every controlled PC, and TCP 3000 reachable from the parent's browser

The stack uses `restart: unless-stopped`, so the server comes back on its own after a host reboot.

### Windows build machine

Only needed to produce agent releases; it is not part of the running system.

- Windows 11 with the .NET 10 SDK
- Node.js 24+ (only for frontend development)

`ControlService`, `SessionAgent`, and `Setup` target `net10.0-windows`, the last two are WPF, and the single-file installer is assembled with IExpress. That build cannot run on Linux, so `scripts/build-agent.ps1` is the one PowerShell script the project still keeps.

### Controlled PC

- Windows 11
- a child account that is a Standard User
- an administrator who can approve the normal Windows setup prompt

The agent publishes self-contained, so the controlled PC does not need .NET installed.

## Start the server

Clone the repository on the Ubuntu host and run first-time initialization. It creates random database, JWT, HTTPS-certificate, and parent-account secrets, generates the self-signed server certificate, and creates the two host directories the server container mounts:

```bash
sudo ./scripts/initialize-server.sh --admin-email "parent@example.com"
```

If `--admin-password` is omitted, a strong password is generated. Every secret is written to the untracked `.env` file with `0600` permissions. The certificate lands in `/opt/kidtime/certs/kidtime.pfx` and its SHA-256 pin — the value the Windows agent pins — in `/opt/kidtime/certs/kidtime.sha256`. Pass `--host` once per additional name or address that belongs in the certificate, and `--agent-url` when the controlled PCs reach the server by a name rather than the detected address.

### Deploy with Portainer

1. **Stacks → Add stack → Repository**, pointing at this repository, with `compose.yaml` as the Compose path.
2. Under **Environment variables**, switch to advanced mode and paste the contents of the generated `.env` (`cat .env`).
3. **Deploy the stack.** Portainer builds the server and web images on the host, which takes a few minutes the first time.

Later updates are a **Pull and redeploy** on the same stack.

### Deploy with the Compose CLI

```bash
docker compose up --build -d
```

Either way, open `http://<server-address>:3000` from the parent's browser. The agent API is exposed at `https://<server-address>:5081`; the web container reaches the API over the private Compose network, so the parent's browser never sees the self-signed certificate.

### Publish behind a reverse proxy

A host that already terminates TLS for other sites can serve KidTime from the same domain. That also gives the agent a publicly trusted certificate instead of the pinned self-signed one. Initialize with the proxy settings so the containers publish on loopback only:

```bash
sudo ./scripts/initialize-server.sh --admin-email "parent@example.com" --bind 127.0.0.1 --web-port 3010 --base-path /kidtime --agent-url https://example.org/kidtime-api
```

`--base-path` is compiled into the web bundle, so changing it later means rebuilding the web image. Route two prefixes to the stack: the panel keeps its prefix, the agent API has its prefix stripped.

```caddyfile
@kidtime_api path /kidtime-api/*
handle @kidtime_api {
	uri strip_prefix /kidtime-api
	reverse_proxy https://127.0.0.1:5081 {
		transport http {
			tls_insecure_skip_verify
		}
	}
}

# Next.js owns the whole prefix and already redirects /kidtime/ to /kidtime, so adding a
# bare-path redirect to the trailing-slash form here would bounce against it forever.
@kidtime path /kidtime /kidtime/*
handle @kidtime {
	reverse_proxy 127.0.0.1:3010
}
```

The proxy hop stays on HTTPS so the server container keeps using its own certificate to encrypt the Data Protection keys; `tls_insecure_skip_verify` covers only that loopback hop. The agent validates the public certificate by chain and consults the pin only when chain validation fails, so ordinary certificate renewals do not disturb enrolled PCs.

Stop without deleting PostgreSQL data:

```bash
docker compose down
```

View status and logs — Portainer's container view shows the same output:

```bash
docker compose ps
docker compose logs --tail 200 server web postgres
```

## Development startup

The API and web projects are cross-platform, so this works on the Ubuntu host or on a workstation. Compose interpolates the whole file even when a single service is started, so a complete `.env` has to be present either way. Start PostgreSQL, then run API and web separately if hot reload is useful:

```bash
docker compose up -d postgres
export ConnectionStrings__KidTime="Host=localhost;Port=5432;Database=kidtime;Username=kidtime;Password=<from .env>"
export Jwt__SigningKey="<from .env>"
export Admin__Email="<from .env>"
export Admin__Password="<from .env>"
dotnet run --project src/KidTime.Server
```

```bash
cd src/kidtime-web
export KIDTIME_API_URL="https://localhost:5081"
npm run dev
```

For a self-signed API certificate, the containerized web path is the supported development setup because it uses the internal HTTP endpoint. A production installation should replace the generated certificate with one trusted for the server's hostname; the Windows agent pins the certificate either way.

## Database migrations

Restore the pinned local EF tool and create a migration:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> \
  --project src/KidTime.Server/KidTime.Server.csproj \
  --startup-project src/KidTime.Server/KidTime.Server.csproj \
  --output-dir Data/Migrations
```

The server applies checked-in migrations when it starts. Do not use `EnsureCreated` against the PostgreSQL database.

## Build and enroll the Windows agent

Agent releases are built on the Windows machine and then uploaded to the Ubuntu server. On Windows, publish the self-contained service, automatic-update ZIP, and consumer setup executable together:

```powershell
./scripts/build-agent.ps1
```

Copy the resulting `artifacts/releases` directory to the server, then publish it there:

```bash
scp -r artifacts/releases <user>@<server-address>:~/kidtime-release
```

```bash
./scripts/publish-agent-release.sh ~/kidtime-release
```

`publish-agent-release.sh` verifies the uploaded package against the size and SHA-256 in `latest.json` before installing it, and writes the manifest last so the server never advertises a release whose package has not fully landed. The server reads that directory on every request, so nothing has to be restarted or rebuilt.

This makes `KidTimeSetup.exe` available through the signed-in web panel. Controlled-device users never need PowerShell, SSH, .NET, or a ZIP extractor.

To add a PC, open **Devices → Add device** in the web panel and follow the three steps:

1. **Download setup.** The panel shows the published version, size, and SHA-256 of `KidTimeSetup.exe`. Download it and copy it to the child's PC.
2. **Copy the code.** The panel creates a one-time, 30-minute enrollment code and shows a live countdown. The single **setup code** carries the server URL, the enrollment code, and the server certificate fingerprint together; the server URL and enrollment code are also shown separately for manual entry. An expired code can be replaced in place.
3. **Connect.** Open setup on the child's PC and approve the normal Windows administrator prompt. Setup asks for the server URL and enrollment code (pasting a setup code fills both), then for the child's Standard User account. **Connect** stays disabled until all three required options are valid. The web panel window changes to **connected** by itself and links straight to the new device controls.

Setup installs the service under `C:\Program Files\KidTime`, protects its data under `C:\ProgramData\KidTime`, configures the `KidTimeControl` LocalSystem service for automatic controlled-PC startup, enrolls the chosen Windows SID, and starts the service. The selected account is controllable as soon as the web panel confirms the connection. Setup does not disable Defender, UAC, the firewall, or any other Windows protection.

The setup window itself is a WPF UI Fluent wizard: a Mica window with a three-step rail, per-field validation messages, a live account list that never offers administrator or disabled profiles, a review summary, and a progress bar during installation.

### Automatic service updates

`build-agent.ps1` also publishes `artifacts/releases/latest.json` and a versioned ZIP. Compose mounts the server's release directory (`/opt/kidtime/releases` by default) read-only into the API. Every enrolled Windows service checks the authenticated update endpoint periodically (five minutes by default), downloads a newer package over the certificate-pinned HTTPS connection, verifies its exact size and SHA-256 hash, stages it under `C:\ProgramData\KidTime\updates`, and installs it through a hidden LocalSystem helper. The helper keeps a local rollback copy and restarts `KidTimeControl`; enrollment and cached rules remain in ProgramData and are not replaced.

Publishing a newer version is the same build-and-upload pair:

```powershell
./scripts/build-agent.ps1
```

```bash
./scripts/publish-agent-release.sh ~/kidtime-release
```

Enrolled PCs pick the release up on their next update check; no deployment to the controlled PCs is needed. The **Devices** list and device detail page show the installed version, published version, update progress, and whether the services are current.

The account chosen in setup is stored by Windows SID, so account renames do not broaden the enforcement scope. It can be changed later under **Devices → device settings → Controlled Windows account**; KidTime never allows an administrator profile to be selected.

## Agent lifecycle and logs

Server-side logs come from Docker, either in Portainer's container view or on the host:

```bash
docker compose logs --tail 200 server web postgres
```

On the controlled PC, as an administrator:

- service state: `Get-Service KidTimeControl`, restart with `Restart-Service KidTimeControl`
- service data/rules/queue: `C:\ProgramData\KidTime\agent.db`
- service config: `C:\ProgramData\KidTime\agentsettings.json`
- service logs: `C:\ProgramData\KidTime\logs\control-service.ndjson`
- update staging/status: `C:\ProgramData\KidTime\updates`
- user-session logs: `%LOCALAPPDATA%\KidTime\logs\session-agent.ndjson`

Logs are newline-delimited JSON. They include connectivity, rule revisions, discovery, blocks, synchronization, and SessionAgent restarts, but never credentials or enrollment tokens.

## Controlled-user tray and status window

SessionAgent places a shield icon in the controlled user's notification area. Left-clicking it opens a modern Fluent/Mica dashboard built from WPF UI's maintained Windows 11-style controls. The dashboard makes the remaining daily allowance visual with a determinate time ring, shows progress through the current weekly-schedule window, and presents every limited or blocked application as a card with its own state badge, allowance bar, and schedule summary. Server connection, synchronization, controlled profile, and cached rule revision are separate visual status cards. It follows the current Windows light/dark theme and accent color automatically.

Right-clicking the tray icon shows server connection, last synchronization, the controlled Windows profile, and **Open KidTime**. The status view is supplied by the privileged service over the existing authenticated, process-validated named pipe; it does not let the Standard User edit or bypass rules. Offline status is explicit and cached rules remain enforced.

The bottom of the screen-time window also offers **Remove KidTime**. Removal requires the parent’s KidTime email address and password and an online connection to the pinned server. After the server verifies those credentials, it deletes the device record and credential, then the LocalSystem helper removes `KidTimeControl`, the installed program files, cached rules, usage data, and the controlled profile’s KidTime notification registration. Windows administrator access by itself does not authorize this GUI flow, and removal cannot proceed offline.

To remove only the server record, open **Devices → device settings → Remove device** in the parent web panel. This permanently deletes that device’s server rules, usage, application associations, enrollment record, and credentials, but deliberately does not reach into the PC or uninstall its services. The local **Remove KidTime** flow remains usable afterward because it authenticates with the parent account independently of the revoked device credential.

The dashboard and tray use `WPF-UI` and `WPF-UI.Tray` 4.3.0. KidTime composes their existing FluentWindow, Card, ProgressRing, InfoBar, Badge, SymbolIcon, menu, and tray controls; it does not maintain a custom widget toolkit.

## Offline enforcement

The SQLite transaction path is:

```text
receive validated rules → atomically replace cached snapshot
active foreground sample → increment local daily totals and pending totals
pending totals → durable idempotent batch → HTTPS upload → delete after success
```

If the server is unavailable, the service continues evaluating the cached PC and application rules. New usage accumulates locally. After reconnection it uploads discovered applications first, uploads durable usage batches, refreshes rules, acknowledges commands, and resumes SignalR.

## Tests and verification

The full solution includes `net10.0-windows` projects, so it is tested on the Windows build machine:

```powershell
dotnet test KidTime.slnx
Set-Location src/kidtime-web
npm run build
```

On Linux, the cross-platform half still runs:

```bash
dotnet test tests/KidTime.Domain.Tests
cd src/kidtime-web && npm run build
```

Automated tests cover daily limits, manual blocks, temporary-block expiry, schedules and overnight windows, timezone day changes, update-tolerant application identity, installer/runtime identity reconciliation, helper-process filtering, rule-change notifications, persisted first-block grace, automatic-update version comparison, controlled-account SID isolation, cached offline rules, durable pending usage, idle exclusion, and cached app-limit evaluation.

Integration checks on the VM should use a harmless executable such as Notepad before testing game rules:

1. allow Notepad and observe foreground usage;
2. block Notepad in the web panel and launch it again;
3. set a one-minute Notepad limit and verify it closes at exhaustion;
4. disconnect only the VM from the server, launch a cached-blocked app, and confirm it remains blocked;
5. reconnect and confirm pending statistics upload;
6. manually block the PC, confirm the native final-warning notification appears with the 60-second grace period, expires instead of leaving a topmost window behind, and confirm Windows signs the session out;
7. sign in again while the rule is active and confirm the warning/sign-out cycle repeats;
8. end SessionAgent as the Standard User and confirm the service restarts it, while PC sign-out enforcement remains independent.

On a disposable PC, also verify enrollment end to end: open **Add device**, download the setup file, confirm **Connect** stays disabled until the server URL, enrollment code, and child account are all valid, confirm an expired code is rejected, and confirm the web panel switches to **connected** on its own.

On a disposable enrolled test PC, also verify both removal paths: remove the server record in the web panel and confirm the card/history disappear, then open the cached screen-time window, select **Remove KidTime**, confirm invalid parent credentials are rejected, confirm valid credentials remove `KidTimeControl`, and verify both `C:\Program Files\KidTime` and `C:\ProgramData\KidTime` are gone.

## Troubleshooting

- **Setup cannot reach the API:** verify the controlled PC can connect to TCP 5081 on the Ubuntu server and that the URL shown by Add device resolves from the controlled PC. Check the host firewall (`sudo ufw status`) if the port is filtered. The one-time code carries the self-hosted server certificate pin automatically.
- **Setup says no standard account was found:** create or enable a Standard User account in Windows Settings, then select **Refresh** in setup. Administrator and disabled accounts are intentionally excluded.
- **Add device says the setup file is unavailable:** run `./scripts/build-agent.ps1` on the Windows build machine, upload `artifacts/releases`, and publish it with `./scripts/publish-agent-release.sh`. If the files are already in `/opt/kidtime/releases`, confirm they are world-readable — the server container runs as a non-root user.
- **Setup reports that this PC is already connected:** remove KidTime from that PC first with **Remove KidTime** in its screen-time window; a second enrollment of the same PC is refused deliberately.
- **Service starts but no UI agent appears:** confirm the signed-in profile is the Standard User selected under **Controlled Windows account**; inspect service logs for `WTSQueryUserToken`/`CreateProcessAsUser` failures.
- **A newly created child profile is not selectable:** wait up to one minute for the service to report local accounts, refresh the device page, and confirm the account is enabled and is not an administrator.
- **Rules show pending:** verify `LastSeenUtc`, the service’s HTTPS connectivity, and that the server URL uses the Ubuntu host's LAN address rather than `localhost`.
- **An app is not listed:** start it once. Only parent-manageable user applications are registered; Windows infrastructure, services, helpers, runtimes, updaters, and KidTime components are intentionally excluded.
- **Usage is lower than elapsed login time:** this is expected. Only non-idle foreground time counts.
- **Changing `.env` admin password has no effect:** the environment variables seed only the first parent account. Use a future password-change flow or update the stored password hash deliberately; do not delete PostgreSQL data merely to rotate a password.
