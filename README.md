# KidTime

KidTime is a self-hosted Windows screen-time and application-control system. The server and web
panel run as Docker containers on an Ubuntu host, typically managed with Portainer; a privileged
`ControlService` and an interactive `SessionAgent` run on the controlled Windows 11 PC.

KidTime deliberately does **not** collect or filter websites, DNS queries, browser history,
searches, messages, keystrokes, screenshots, camera/microphone data, or network traffic.

```text
Parent browser ──> Next.js web proxy ──JWT──> ASP.NET Core API ──> PostgreSQL
                                                    │ HTTPS + SignalR
Controlled PC:  ControlService (LocalSystem) ──pipe──> SessionAgent (logged-in user)
                cached rules, enforcement,            foreground/idle detection,
                usage queue, sign-out                 tray status, notifications
```

Architecture, security decisions, development commands, verification steps, and troubleshooting are
in [CLAUDE.md](CLAUDE.md).

## Requirements

- **Server:** Ubuntu 22.04+ with Docker Engine and the Compose plugin, Portainer optional. TCP 5081
  reachable from every controlled PC, TCP 3000 from the parent's browser.
- **Windows build machine** (only to produce agent releases): Windows 11 with the .NET 10 SDK, plus
  Node.js 24+ for frontend work.
- **Controlled PC:** Windows 11, a child account that is a Standard User, and an administrator who
  can approve the normal Windows setup prompt. The agent publishes self-contained, so .NET is not
  needed there.

## Start the server

```bash
sudo ./scripts/initialize-server.sh --admin-email "parent@example.com"
```

This writes every generated secret to the untracked `.env` (mode `0600`), creates the self-signed
certificate and its SHA-256 pin in `/opt/kidtime/certs`, and creates the host directories the server
container mounts. Useful flags: `--admin-password`, `--host` (extra certificate names), `--agent-url`
(how controlled PCs reach the API), and `--bind` / `--base-path` / `--web-port` for a reverse proxy.

Deploy with the Compose CLI:

```bash
docker compose up --build -d
```

Or with Portainer: **Stacks → Add stack → Repository** pointing at this repository with
`compose.yaml` as the Compose path, paste the output of `cat .env` into the advanced environment
variables, and deploy. Later updates are **Pull and redeploy** on the same stack.

The panel is then at `http://<server-address>:3000` and the agent API at
`https://<server-address>:5081`. The web container reaches the API over the private Compose network,
so the parent's browser never sees the self-signed certificate. The stack uses
`restart: unless-stopped`, so it returns on its own after a host reboot.

## Publish the Windows agent

On the Windows build machine:

```powershell
./scripts/build-agent.ps1
```

Upload the result and publish it on the server:

```bash
scp -r artifacts/releases <user>@<server-address>:~/kidtime-release
```

```bash
./scripts/publish-agent-release.sh ~/kidtime-release
```

This makes `KidTimeSetup.exe` and the automatic-update package available. Enrolled PCs pick up a new
version on their next update check; nothing has to be deployed to them, and the server does not need
a restart.

## Add a PC

Open **Devices → Add device** in the panel and follow the three steps: download `KidTimeSetup.exe`,
copy the one-time setup code (valid 30 minutes; it carries the server URL, enrollment code, and
certificate fingerprint together), then run setup on the child's PC, approve the Windows
administrator prompt, and pick the Standard User account. The panel switches to **connected** by
itself.

Setup installs into `C:\Program Files\KidTime`, keeps protected data in `C:\ProgramData\KidTime`,
registers the `KidTimeControl` LocalSystem service, and enrolls the chosen Windows SID. It does not
disable Defender, UAC, the firewall, or any other Windows protection.

## Operate

```bash
docker compose ps
docker compose logs --tail 200 server web postgres
docker compose down            # stops without deleting PostgreSQL data
```

On the controlled PC, as an administrator:

- service state: `Get-Service KidTimeControl`, `Restart-Service KidTimeControl`
- rules, usage queue, config, logs, updates: `C:\ProgramData\KidTime`
- user-session log: `%LOCALAPPDATA%\KidTime\logs\session-agent.ndjson`

Logs are newline-delimited JSON covering connectivity, rule revisions, discovery, blocks, and
synchronization; they never contain credentials or enrollment tokens.

Crashes and errors on a controlled PC do not stay there. Both Windows components report them to the
server on the next synchronization, and the panel's **Error log** page shows each one with the
device, component, severity, how often it recurred, the agent version, and its stack trace. That is
the place to look once a PC has been handed over and is no longer convenient to sit at.

Everything the controlled PC shows the child is available in **English or Russian**, chosen per
device under **Devices → device settings → Language on the PC**. It covers notifications, the
countdown card, the tray menu, and the screen-time window, and takes effect on the next
synchronization with no reinstall. The parent panel itself stays in English.

The controlled user's tray icon opens a read-only screen-time window with four tabs: today's
allowance, the apps that have limits, connection state, and an About tab showing the installed
version and what KidTime does and does not see. Its **Remove KidTime** button uninstalls the
PC side, but only after the parent's KidTime email and password verify against the server, so it
cannot be used offline or by a local administrator alone. **Devices → device settings → Remove
device** deletes only the server record and deliberately does not reach into the PC.
