# KidTime

KidTime is a self-hosted Windows screen-time and application-control system. The server and web
panel run as Docker containers on an Ubuntu host, typically managed with Portainer; a privileged
`ControlService` and an interactive `SessionAgent` run on the controlled Windows 11 PC.

KidTime deliberately does **not** collect or filter websites, DNS queries, browser history,
searches, messages, keystrokes, screenshots, camera/microphone data, or network traffic. It can be
pointed at a [Technitium DNS server](#web-filtering-optional) that does filter, and then reports
what that server is configured to block - never what was looked up.

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

On the Windows build machine, once:

```powershell
./scripts/release-agent.ps1 -Server root@<server-address>
```

and from then on:

```powershell
./scripts/release-agent.ps1
```

That bumps the patch version, builds, uploads over SSH, and publishes on the server. It needs the
Windows OpenSSH client and a working SSH login; the server is remembered after the first run. Add
`-Bump minor` or `-Version 2.0.0` for a different number, `-Bump none` to re-publish the current one,
or `-SkipPublish` to build without releasing. Commit the version change in `Directory.Build.props`
together with the work it ships.

From a Linux workstation, which cannot build the agent half, the same release runs over SSH against
a Windows build machine:

```bash
./scripts/release-agent.sh --build-host parent@<windows-address> --identity ~/.ssh/id_ed25519
```

It bumps the version, sends the working tree over, builds it there with the same script, brings the
release back, and publishes it on `--server user@host` — or on this machine when no server is given,
which is what a workstation that also runs the stack wants. `--vm <libvirt-domain>` starts a local
Windows VM first and finds its address itself. That machine needs the .NET SDK and OpenSSH and
nothing else; it is remembered after the first run, and the same `--bump`, `--version`,
`--skip-publish` and `--release-dir` flags apply.

The two steps are still available separately — `./scripts/build-agent.ps1` on Windows, then
`./scripts/publish-agent-release.sh <uploaded-directory>` on the server.

This makes `KidTimeSetup.exe` and the automatic-update package available. Enrolled PCs pick up a new
version on their next update check; nothing has to be deployed to them, and the server does not need
a restart.

## Web filtering (optional)

Website filtering is not KidTime's job and never becomes it. If the household already runs
[Technitium DNS](https://technitium.com/dns/) with the
[DNS Companion](https://github.com/fail-safe/technitium-dns-companion), fill in the `KIDTIME_DNS_*`
variables in `.env` and KidTime will read that configuration - read only, and never the query logs:

- the panel grows a **Web filtering** page: whether filtering is on, what it covers, and a button
  that opens the DNS console, which is where it is actually set up;
- the child's KidTime window grows an **Internet** tab listing what is blocked all the time and
  which sites have hours of their own, with the time they come back;
- when a filtered site will not open in the child's Firefox, KidTime says why - once, as an
  ordinary notification naming what the home network blocks and when a closed set of sites comes
  back. It explains the rule and never the site: KidTime does not read the address, the DNS query
  log, or anything else that would amount to browsing history, so the message is the same sentence
  whatever was typed. A PC that cannot reach the server stays quiet, because a home network that is
  down produces the same browser error page as a blocked site.

With the variables empty the page says so and the child's PC shows no such tab. Nothing about
KidTime's own enforcement changes either way: a DNS block and a KidTime rule are separate things,
and neither can stand in for the other.

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
docker compose logs -f server web postgres
docker compose down            # stops without deleting PostgreSQL data
```

The server and the panel log in the same format - time, level, source, message - and every request
carries a `req=` id that appears on both containers' lines and on the response's `X-Request-Id`
header, so one click in the panel can be followed all the way through. PostgreSQL logs statements
slower than half a second, never the statements themselves. Four optional variables in `.env` tune
all of it:

| Variable | Default | Effect |
| --- | --- | --- |
| `KIDTIME_LOG_LEVEL` | `Information` | `Debug` adds health probes, hub traffic, and every API call the panel makes. |
| `KIDTIME_LOG_FORMAT` | `pretty` | `json` emits structured events for a log collector instead. |
| `KIDTIME_LOG_COLOR` | `always` | `never` for a terminal or pipeline that does not render ANSI. |
| `KIDTIME_PG_SLOW_QUERY_MS` | `500` | The duration above which PostgreSQL logs a query's shape. |

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

The controlled user's tray icon opens a read-only screen-time window with four tabs - today's
allowance, the apps that have limits, connection state, and an About tab showing the installed
version and what KidTime does and does not see - plus an **Internet** tab when a DNS filter is
connected. Its **Remove KidTime** button uninstalls the PC side, but only after the parent's
KidTime email and password verify against the server, so it cannot be used offline or by a local
administrator alone. **Devices → device settings → Remove
device** deletes only the server record and deliberately does not reach into the PC.
