# KidTime

KidTime is a self-hosted Windows screen-time and application-control system. The server and web panel run on the parent PC; a privileged `ControlService` and interactive `SessionAgent` run on the controlled Windows 11 PC.

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
                                                   ├── persistent final warning + Windows sign-out
                                                   └── secured named pipe
                                                          │
                                                   SessionAgent (logged-in user)
                                                   ├── foreground app + idle detection
                                                   └── native Windows app notifications
```

- REST handles enrollment, heartbeats, rules, usage batches, discovery, and administration.
- SignalR wakes the agent for immediate commands. Periodic sync remains the fallback and SignalR is never required for enforcement.
- Parent JWTs live only in a secure, HTTP-only, same-site cookie managed by Next.js.
- Each device receives a random credential after redeeming a single-use enrollment token. Only a SHA-256 digest is stored on the server.
- The service caches the last valid rule snapshot in SQLite and never changes to “allow everything” merely because the server is unavailable.
- The app catalog exposes parent-manageable user apps rather than Windows services, helpers, update components, or KidTime itself.

See [Architecture and security](docs/architecture-security.md) for rule precedence, time accounting, IPC, identity, and first-version anti-tamper boundaries.

## Prerequisites

### Parent/server PC

- Windows with Docker Desktop running manually
- .NET 10 SDK (only for development, migration, tests, and agent builds)
- Node.js 24+ (only for frontend development)
- OpenSSH client for VM deployment

No Scheduled Task, Windows service, or Docker autostart entry is installed on the parent PC.

### Controlled PC

- Windows 11
- a child account that is a Standard User
- OpenSSH Server reachable from the parent PC
- an administrator account available through SSH for installing the LocalSystem service

The agent publishes self-contained, so the controlled PC does not need .NET installed.

## Start the server

First-time initialization creates random database, JWT, HTTPS-certificate, and parent-account secrets:

```powershell
./scripts/initialize-server.ps1 -AdminEmail "parent@example.com"
```

If `-AdminPassword` is omitted, a strong password is generated. It is stored in the untracked `.env` file. The script also creates an HTTPS development certificate and writes its SHA-256 pin to `.data/certs/kidtime.sha256`.

Start the stack manually:

```powershell
docker compose up --build -d
```

Open `http://localhost:3000`. The agent API is exposed at `https://<parent-pc-address>:5081`; the browser web service reaches the API over the private Compose network.

Stop without deleting PostgreSQL data:

```powershell
docker compose down
```

View status and logs:

```powershell
docker compose ps
docker compose logs --tail 200 server web postgres
```

The Compose services intentionally use `restart: "no"`; they do not come back automatically when Docker starts.

## Development startup

Start PostgreSQL, then run API and web separately if hot reload is useful:

```powershell
docker compose up -d postgres
$env:ConnectionStrings__KidTime = "Host=localhost;Port=5432;Database=kidtime;Username=kidtime;Password=<from .env>"
$env:Jwt__SigningKey = "<from .env>"
$env:Admin__Email = "<from .env>"
$env:Admin__Password = "<from .env>"
dotnet run --project src/KidTime.Server
```

```powershell
Set-Location src/kidtime-web
$env:KIDTIME_API_URL = "https://localhost:5081"
npm run dev
```

For a self-signed API certificate, the containerized web path is the supported development setup because it uses the internal HTTP endpoint. A production installation should replace the generated certificate with one trusted for the parent PC hostname.

## Database migrations

Restore the pinned local EF tool and create a migration:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef migrations add <MigrationName> `
  --project src/KidTime.Server/KidTime.Server.csproj `
  --startup-project src/KidTime.Server/KidTime.Server.csproj `
  --output-dir Data/Migrations
```

The server applies checked-in migrations when it starts. Do not use `EnsureCreated` against the PostgreSQL database.

## Build and enroll the Windows agent

1. Start the server and sign in to the web panel.
2. Go to **Settings** and create a 30-minute, one-time enrollment token.
3. Find a LAN address for the parent PC that the VM can reach.
4. Read the certificate pin from `.data/certs/kidtime.sha256`.
5. Build and deploy through SSH:

```powershell
./scripts/build-agent.ps1
./scripts/deploy-agent.ps1 `
  -ComputerName "192.168.0.201" `
  -UserName "<administrator>" `
  -ServerUrl "https://<parent-pc-address>:5081" `
  -EnrollmentToken "<one-time-token>" `
  -CertificatePin (Get-Content -Raw .data/certs/kidtime.sha256)
```

The deployment copies the self-contained package through SCP, stops the previous service when present, installs to `C:\Program Files\KidTime`, enrolls only when no device credential exists, configures the `KidTimeControl` LocalSystem service for automatic controlled-PC startup, and confirms it reaches `Running`. This is also the one-time bootstrap for automatic service updates.

The SSH session must have an elevated administrator token. The scripts do not disable Defender, UAC, firewall, or any other Windows protection.

### Automatic service updates

`build-agent.ps1` also publishes `artifacts/releases/latest.json` and a versioned ZIP. Compose mounts that release directory read-only into the API. Every enrolled Windows service checks the authenticated update endpoint periodically (five minutes by default), downloads a newer package over the certificate-pinned HTTPS connection, verifies its exact size and SHA-256 hash, stages it under `C:\ProgramData\KidTime\updates`, and installs it through a hidden LocalSystem helper. The helper keeps a local rollback copy and restarts `KidTimeControl`; enrollment and cached rules remain in ProgramData and are not replaced.

Build a newer version and rebuild the server to publish it:

```powershell
./scripts/build-agent.ps1
docker compose up --build -d
```

No SSH deployment is needed after the bootstrap. The **Devices** list and device detail page show the installed version, published version, update progress, and whether the services are current.

After the service reports its first heartbeat, open **Devices → device settings → Controlled Windows account**. Select the child’s enabled Standard User profile and save. KidTime remains inactive until an account is selected, and it will not allow an administrator profile to be selected. The account is stored by Windows SID, so account renames do not broaden the enforcement scope.

## Agent lifecycle and logs

Restart and inspect through SSH:

```powershell
./scripts/restart-agent.ps1 -ComputerName "192.168.0.201" -UserName "<administrator>"
./scripts/collect-logs.ps1 -ComputerName "192.168.0.201" -UserName "<administrator>" -Lines 300
```

On the controlled PC:

- service state: `Get-Service KidTimeControl`
- service data/rules/queue: `C:\ProgramData\KidTime\agent.db`
- service config: `C:\ProgramData\KidTime\agentsettings.json`
- service logs: `C:\ProgramData\KidTime\logs\control-service.ndjson`
- update staging/status: `C:\ProgramData\KidTime\updates`
- user-session logs: `%LOCALAPPDATA%\KidTime\logs\session-agent.ndjson`

Logs are newline-delimited JSON. They include connectivity, rule revisions, discovery, blocks, synchronization, and SessionAgent restarts, but never credentials or enrollment tokens.

## Offline enforcement

The SQLite transaction path is:

```text
receive validated rules → atomically replace cached snapshot
active foreground sample → increment local daily totals and pending totals
pending totals → durable idempotent batch → HTTPS upload → delete after success
```

If the server is unavailable, the service continues evaluating the cached PC and application rules. New usage accumulates locally. After reconnection it uploads discovered applications first, uploads durable usage batches, refreshes rules, acknowledges commands, and resumes SignalR.

## Tests and verification

```powershell
dotnet test KidTime.slnx
Set-Location src/kidtime-web
npm run build
```

Automated tests cover daily limits, manual blocks, temporary-block expiry, schedules and overnight windows, timezone day changes, update-tolerant application identity, installer/runtime identity reconciliation, helper-process filtering, rule-change notifications, persisted first-block grace, automatic-update version comparison, controlled-account SID isolation, cached offline rules, durable pending usage, idle exclusion, and cached app-limit evaluation.

Integration checks on the VM should use a harmless executable such as Notepad before testing game rules:

1. allow Notepad and observe foreground usage;
2. block Notepad in the web panel and launch it again;
3. set a one-minute Notepad limit and verify it closes at exhaustion;
4. disconnect only the VM from the parent server, launch a cached-blocked app, and confirm it remains blocked;
5. reconnect and confirm pending statistics upload;
6. manually block the PC, confirm the non-modal countdown banner remains visible for 60 seconds, and confirm Windows signs the session out;
7. sign in again while the rule is active and confirm the warning/sign-out cycle repeats;
8. end SessionAgent as the Standard User and confirm the service restarts it, while PC sign-out enforcement remains independent.

## Troubleshooting

- **Enrollment cannot reach the API:** verify the VM can connect to TCP 5081 on the parent PC, the URL uses HTTPS, and the certificate pin is the exact content of `kidtime.sha256`.
- **Service starts but no UI agent appears:** confirm the signed-in profile is the Standard User selected under **Controlled Windows account**; inspect service logs for `WTSQueryUserToken`/`CreateProcessAsUser` failures.
- **A newly created child profile is not selectable:** wait up to one minute for the service to report local accounts, refresh the device page, and confirm the account is enabled and is not an administrator.
- **Rules show pending:** verify `LastSeenUtc`, the service’s HTTPS connectivity, and that the server URL uses an address reachable from the VM rather than `localhost`.
- **An app is not listed:** start it once. Only parent-manageable user applications are registered; Windows infrastructure, services, helpers, runtimes, updaters, and KidTime components are intentionally excluded.
- **Usage is lower than elapsed login time:** this is expected. Only non-idle foreground time counts.
- **Changing `.env` admin password has no effect:** the environment variables seed only the first parent account. Use a future password-change flow or update the stored password hash deliberately; do not delete PostgreSQL data merely to rotate a password.
