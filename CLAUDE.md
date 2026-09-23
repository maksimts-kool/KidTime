# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

It is also the repository's only long-form document. Architecture, security rationale, verification
steps, and troubleshooting live here; `README.md` stays a compact deployment guide and should not
grow prose that belongs in this file.

## What this is

KidTime is a self-hosted Windows screen-time and application-control system. Two halves live in one
repo:

- **Server half** (cross-platform, Docker on Ubuntu): `src/KidTime.Server` (ASP.NET Core API +
  SignalR + PostgreSQL) and `src/kidtime-web` (Next.js parent panel).
- **Agent half** (Windows-only, installed on the controlled PC): `src/KidTime.ControlService`
  (LocalSystem worker service — the enforcement boundary), `src/KidTime.SessionAgent` (WPF tray UI
  in the child's session), `src/KidTime.Setup` (WPF installer, `KidTimeSetup.exe`).
- `src/KidTime.Domain` is shared by both halves: rule models and evaluator, wire contracts,
  application identity and catalog policy, and the localized string catalog the controlled PC
  speaks from. Anything both sides must agree on belongs here.

## Commands

The full solution contains `net10.0-windows` projects, so a complete build/test only works on
Windows. On Linux only the cross-platform half builds.

```powershell
dotnet build KidTime.slnx
dotnet test KidTime.slnx
```

The two cross-platform test projects run anywhere:

```bash
dotnet test tests/KidTime.Domain.Tests && dotnet test tests/KidTime.Server.Tests
```

Single test or class (xunit via VSTest):

```bash
dotnet test tests/KidTime.Domain.Tests --filter "FullyQualifiedName~RuleEvaluatorTests"
```

Web panel — `npm run build`, `npm run lint`, and `npm run dev` (which needs `KIDTIME_API_URL`
exported):

```bash
cd src/kidtime-web && npm run build
```

Server locally, with `ConnectionStrings__KidTime`, `Jwt__SigningKey`, `Admin__Email`, and
`Admin__Password` exported from `.env` and `docker compose up -d postgres` already running. Compose
interpolates the whole file even when a single service starts, so a complete `.env` must exist
either way:

```bash
dotnet run --project src/KidTime.Server
```

For a self-signed API certificate, the containerized web path is the supported development setup,
because it reaches the API over the internal HTTP endpoint instead of the pinned one.

EF migrations use the repo-pinned local tool and land in `Data/Migrations`:

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> --project src/KidTime.Server/KidTime.Server.csproj --startup-project src/KidTime.Server/KidTime.Server.csproj --output-dir Data/Migrations
```

`DatabaseInitializer` applies checked-in migrations at startup and seeds the first parent account
from `Admin__*`. Never `EnsureCreated` against PostgreSQL. The `Admin__*` variables seed only the
first account; they do not rotate an existing password.

Windows agent release (Windows build machine only — WPF plus IExpress):

```powershell
./scripts/release-agent.ps1
```

That is the whole release. It bumps the patch version in `Directory.Build.props`, runs
`build-agent.ps1`, uploads the result over SSH, and runs `publish-agent-release.sh` on the server —
the four steps a release always needed, including the version bump, which is the one that fails
silently when it is forgotten: the agent compares the published manifest with its own assembly
version, so a release reusing the current number reaches nobody. The server is remembered in the
untracked `artifacts/deploy-target.txt` after the first `-Server user@host`, and can also come from
`KIDTIME_SERVER`. Useful flags: `-Bump major|minor|patch|none`, `-Version`, `-SkipPublish`,
`-ReleaseDirectory`. Nothing is assumed to be checked out on the server — the publish script travels
with the payload.

The same release, driven from a Linux workstation that cannot build the agent half itself:

```bash
./scripts/release-agent.sh --vm kidtime-win11 --build-user parent --identity ~/.kidtime/vm/id_ed25519
```

`scripts/release-agent.sh` runs the identical four steps by sending the **working tree** — whatever
`git` does not ignore, including the version bump it has just made — to a Windows machine over SSH,
running `build-agent.ps1` there in a directory it owns and clears on every release
(`C:\kidtime-build`, so a file deleted since the last one cannot survive into a package), and
fetching `artifacts/releases` back. Nothing is assumed to be checked out on that machine either: the
source travels as an archive and the build step as a generated PowerShell script. `--vm` starts a
libvirt domain first and reads its address from the DHCP lease **on every run**, because a machine
that has been off for a while does not keep it; `--build-host user@host` names any other Windows
machine instead, and both are remembered in the untracked `artifacts/build-host.conf`. Publishing
goes to `--server user@host` as it does from Windows, or — with no server configured — happens
here, into `KIDTIME_RELEASE_DIR` as `.env` gives it, which is what a workstation that also runs the
server wants. **That `.env` answers for this machine only.** A workstation that runs its own server
names a path like `~/.kidtime/releases`, and sending that to `--server` fails at the last step of a
release — after the whole agent has been built and uploaded — looking for a directory the server has
never had. So the server is left to resolve its own (`KIDTIME_RELEASE_DIR` there, else
`/opt/kidtime/releases`, which is where `initialize-server.sh` puts it) unless this run passed
`--release-dir` itself. A build whose `latest.json` names a version other than the one asked for is refused
rather than published: from Linux that is what a stale source tree on the build machine looks like.

The two halves still stand alone. `scripts/build-agent.ps1` publishes self-contained single-file
binaries, writes `artifacts/releases/{latest.json, kidtime-agent-<version>.zip, KidTimeSetup.exe}`,
and embeds the same ZIP into setup as a resource. `scripts/publish-agent-release.sh <dir>` installs
that upload on the server, verifying size and SHA-256 before writing `latest.json` last, so the
server never advertises a package that has not fully landed. `scripts/initialize-server.sh`
generates all secrets, the self-signed certificate, and `.env`.

## Architecture

### Enforcement boundary

`ControlService` is the only component trusted to hold device credentials, cache rules, count
accepted samples, decide allow/block state, terminate blocked processes, and supervise
`SessionAgent`. It runs as LocalSystem. `SessionAgent` runs unelevated in the logged-in desktop
because services cannot safely provide ordinary interactive UI.

Enforcement is scoped to exactly one Windows SID. The parent selects one enabled Standard User; its
SID is cached in the rule snapshot, and the service launches SessionAgent, counts usage, terminates
apps, and signs out sessions **only when that exact SID owns the active console session**. With no
selected SID, enforcement is inactive. Administrator accounts are reported but cannot be selected,
and the server rejects an administrator or disabled account at enrollment independently of setup.

The named pipe is a trust boundary, not a convenience channel. `NamedPipeHost` grants
transport-level read/write to authenticated local users so the Standard User's SessionAgent can
connect, but accepts a connection only when the kernel-reported client process ID matches the exact
SessionAgent that `SessionAgentSupervisor` launched and supervises. The SessionAgent process DACL
grants control only to LocalSystem and administrators. Messages are length-bounded and permit
exactly three request shapes: a foreground telemetry sample, a parent-login removal request, or a
batch of fault reports. The reports are inert data - the service stamps the component itself,
truncates every field through `DiagnosticReportPolicy`, and bounds the batch - so the unelevated
agent cannot use them to impersonate the service or reach a privileged operation. **Do not widen
the protocol** to let the unelevated agent submit rules or arbitrary privileged commands.
The service accepts the removal request only after the parent credentials succeed against the
certificate-pinned server, and never logs those credentials.

A local administrator remains outside the security boundary: an administrator can take ownership,
alter files, stop protected services, boot to recovery, or change accounts. KidTime therefore
requires a separate Standard User for the controlled child, and the administrator account used for
deployment must not be the child's everyday account.

### Request paths and authentication

Two authentication schemes coexist on the same API. Parent endpoints use JWT bearer; agent endpoints
(`api/agent/*` and the `/hubs/device` SignalR hub) use `DeviceAuthenticationHandler`, a custom scheme
validating the SHA-256 digest of the device credential. Both are wired in
`src/KidTime.Server/Program.cs`.

The browser never holds a JWT. `src/kidtime-web/app/api/session/route.ts` exchanges the login for an
HTTP-only, strict-same-site cookie; server components read the API through `lib/backend.ts` and
client components through the catch-all proxy `app/api/backend/[...path]/route.ts`. Add new API
calls through those two, never by fetching the backend directly from the browser.

### Rule flow

1. Parent edits rules → `DevicesController` / `ApplicationsController` mutate the rule row,
   **increment `DeviceRule.Revision`**, insert a `DeviceCommand`, and push it over
   `IHubContext<DeviceHub>` to the `device:<id>` group. Revision bumping and the SignalR push must
   stay together — the agent detects change by revision.
2. `RuleSnapshotFactory` flattens device and application rules into the `DeviceRuleSnapshot` that
   crosses the wire, filtering out non-user-manageable apps via `ApplicationCatalogPolicy`.
3. `AgentWorker` is the sync loop: upload discovered apps → upload durable usage batches →
   `GET api/agent/sync` → persist the snapshot to SQLite → `EnforcementCoordinator.UpdateRules` →
   acknowledge commands. SignalR only nudges `SyncTrigger`; periodic polling remains the fallback and
   **enforcement never depends on the hub**.
4. `SessionAgent` samples the foreground window every two seconds and sends it over the named pipe.
   `EnforcementCoordinator.HandleSampleAsync` is the single place that counts time, evaluates
   `RuleEvaluator`, queues notifications, and produces `EnforcementState`.

### Fault reporting

Once a PC is handed over it is normally unreachable, so **every fault has to reach the parent's
panel by itself**. The pipeline is one-way and durable at each hop:

1. `SessionAgent` installs `DispatcherUnhandledException`, `AppDomain.UnhandledException`, and
   `TaskScheduler.UnobservedTaskException` handlers, calls `SetErrorMode` so a crash can never
   leave a Windows error dialog on the child's screen, and writes each fault to a spool file in
   `%LOCALAPPDATA%\KidTime\logs`. A dispatcher fault is marked handled and the agent keeps
   running: while it is gone there are no notifications and no usage samples.
2. The spool travels to `ControlService` on the next pipe exchange and is only deleted after the
   service answers, so a fault survives a crash-restart loop.
3. A fault that stops the agent starting cannot ride a sample, because that copy never reaches
   the sampling loop - and the service relaunches it every two seconds, so each new copy spools
   the same fault and dies with it. `App` therefore makes one bounded, best-effort delivery on
   the way out, and `SessionAgentSupervisor` counts launches that die within fifteen seconds and
   reports a crash loop as an error once per episode. Between them a child sitting with no
   interface is something the parent finds in the error log rather than by noticing.
4. In `ControlService`, `DiagnosticReporter` also receives its own unhandled exceptions and every
   `LogError`/`LogCritical` written through `JsonFileLoggerProvider`. Warnings stay local -
   offline synchronization and IPC retries are expected operation, not defects. Reports are
   appended to a bounded spool, moved into the SQLite queue, and uploaded during synchronization
   before rules and usage, so a PC that fails at a later step still reports why.
5. `POST api/agent/diagnostics` re-normalizes every field, collapses repeats onto one row by
   fingerprint (digits are folded out, so counters and process ids do not fragment one bug),
   ignores a retried upload by `LastReportId`, and caps the history per device. **One batch can
   carry the same fingerprint several times** - the agent's queue is keyed by report id and its
   repeat suppression is in memory, so a service restart re-spools a fault that is already
   queued. `DiagnosticReportPolicy.CollapseBatch` folds a batch onto one entry per fingerprint
   before anything is written, because a second insert for the same `(DeviceId, Fingerprint)`
   violates the unique index and fails the whole transaction - and the agent then retries the
   same poisoned batch forever, so no fault from that PC ever reaches the parent again.
6. The panel's **Error log** page lists unresolved faults with device, component, severity, count,
   agent version, and stack trace, and the devices page badges a PC that has reports waiting.

Repeat suppression exists at both ends: the agent spools one report per fingerprint per five
minutes, and the server counts occurrences instead of inserting rows. Setup runs before any device
exists and therefore cannot report anywhere - it writes `%LOCALAPPDATA%\KidTime\logs\setup.ndjson`
and shows the failure instead.

### Language on the controlled PC

Everything the child reads is localized: Windows notifications, the countdown card, the tray menu
and tooltip, the screen-time window, rule messages, and the removal flow. The parent's panel stays
in English - it is the parent's tool, and mixing the two audiences in one dictionary was not worth
the cost.

`AgentLanguage` (English, Russian) lives on `DeviceRule` and travels inside `DeviceRuleSnapshot`,
so changing it bumps the rule revision and reaches the PC over the ordinary SignalR-nudged sync
path with no separate channel. `EnforcementState.Language` rides on every pipe reply as well, so
the tray agent can paint its own chrome before any status snapshot arrives.

`KidTime.Domain.Localization.AgentStrings` is the single catalog. It is an **abstract class with
one sealed implementation per language**, not a resource dictionary, so a message added in one
language fails to compile in the other instead of silently rendering blank on a child's screen.
Two consequences to respect:

- **Never put a user-visible literal in ControlService or SessionAgent.** Add a member to
  `AgentStrings` and implement it in `EnglishAgentStrings` and `RussianAgentStrings`.
- Composition happens in the language implementation, not at the call site. Russian selects one
  of three plural forms by count and changes case after a preposition, so
  `SignOutCountdownTitle(60)` builds the whole phrase ("через 1 минуту") rather than gluing a
  shared number-and-noun fragment into an English sentence shape. Sentences that embed a duration
  or a deadline are written label-and-value in Russian ("Осталось времени: 15 минут") for the same
  reason.

Strings that are not authored by KidTime stay as they are: `LastSynchronizationError` carries a raw
exception message, and application display names come from the application itself. Connection state
crosses the pipe as `ServerConnectionState`, a code, so the unelevated agent phrases it.

### Extra time

A child whose screen time or app limit is nearly spent can ask their parent for more. **The
request is a question and never a grant.** The unelevated tray agent can only ask; the extra
minutes come back from the parent through the ordinary rule path, which is what keeps the
enforcement boundary where it was.

1. `EnforcementCoordinator` produces a `TimeExtensionOffer` whenever a daily limit has
   `TimeExtensionPolicy.RequestThresholdSeconds` (five minutes) or less left — for the PC, for the
   foreground application, and for **every limited application in the status snapshot**, so the
   Apps tab can put the button on the card of the one that is running out rather than only on
   whatever happens to be in front. The offer deliberately **outlives the block itself** — the
   moment a child most wants to ask is the moment the time ran out and the sign-out card
   appeared — and **anything that is actually shut can be asked about**, a manual block and a
   closed schedule window included, because a grant now lifts those two as well.
2. The child asks from four places, and all of them open the **same popup**: the PC card in the
   Today panel, the button on an application's card in the Apps tab, a button on the countdown
   card, and an action button on the urgent running-out toast. One slider in one dialog serves
   every scope; the entry points differ only in what they are about, and the last two open it
   directly for the thing that is closing. The toast button reaches the running agent through
   `ToastNotificationManagerCompat.OnActivated`, and if that registration ever fails the child
   still has the window and the card — **nothing depends on it**.
3. `TimeExtensionSubmission` crosses the named pipe as the fourth and last shape that protocol
   accepts. `EnforcementCoordinator.RequestTimeExtensionAsync` resolves the scope, measures what
   is actually left from the buffered usage and the limit in force — or, for a scope that is
   already shut, says so, since a block has nothing left to measure — and `TimeExtensionService`
   records the request in SQLite. It is durable before it is uploaded, so a service restart or a
   night offline never swallows a question a child is waiting on.
4. `AgentWorker` uploads pending requests before usage, and `POST api/agent/time-extensions`
   re-checks everything the service checked — nothing arriving over the network is trusted. The
   request id is minted on the PC, so a retry after an uncertain response cannot ask the parent
   the same question twice.
5. The panel's **Requests** page shows what is waiting, on the same slider the child used and
   starting on the amount they asked for, so "yes, but twenty minutes" is one drag and one click. A decision — approve *or* deny —
   bumps `DeviceRule.Revision`, queues a command, and is pushed over the hub, because that is the
   one path the PC already watches, and a child left staring at "waiting for your parent" has been
   told nothing at all.
6. `RuleSnapshotFactory` reads today's approved grants back as a `TimeBonus` on the device rule and
   on each application rule, and `RuleEvaluator.EffectiveDailyLimitSeconds` is where every path
   must read a daily limit from. **A bonus carries the device-local date it was granted for**, so
   it stops applying by itself at midnight with no second message to take it away, and a grant
   approved after the day has turned adds nothing to that day's limit.

   A `TimeBonus` carries a second thing, because the restrictions extra time lifts are not counted
   the same way. `Seconds` raises a daily limit and is spent in **active foreground time** like the
   rest of that allowance. `LiftedUntilUtc` is what the same grant does to a manual block or a
   closed schedule window: neither is an allowance with seconds left in it, so that half runs as
   **wall-clock time from the parent's decision** — `DecidedAtUtc` plus the granted minutes — which
   is the one starting point the child and the parent both saw. `TimeBonus.LiftsBlocksAt` is the
   only thing `RuleEvaluator` consults for it, and only the manual-block and schedule branches
   consult it: a lifted block never hands over a spent daily limit, since the same grant already
   raised that by its own minutes. The window is an absolute instant rather than a date, so one
   opened at ten to midnight runs the minutes it was given; the snapshot therefore reads yesterday's
   grants too, for the window alone.
7. The agent announces the answer once — `TimeExtensionService` marks it announced after the
   notification is queued — as an ordinary toast. Nothing is closing, so nothing interrupts.

The limits are `TimeExtensionPolicy`. The amount is a **slider from 5 to 30 minutes in steps of
five** rather than a number field: a dial with a floor and a ceiling asks a smaller question, and
there is nothing between the stops to argue about. Both ends check it — a value off the grid is
refused by the service and again by the server, because the slider is a convenience and never the
constraint. Beyond that: one pending request per scope, eight requests per PC per day, and a grant
bounded at four hours, which is looser than the slider so a decision made outside it is still
bounded by something. That ceiling is also what bounds the window a grant opens over a manual
block, which is the one case where the minutes are not spent by the child using the PC.

**A refusal holds for the allowance period it was given in.** `RuleEvaluator.GetAllowancePeriodKey`
names that period — the schedule window currently open, or the device-local day when no schedule
is configured — and the request records it. Asked and told no at 14:00 inside an 08:00–15:00
window, the child cannot ask again for that scope until the 18:00 window opens; then the key
changes and the buttons come back on their own. That is the difference between a child who may ask
and a child who may pester, and it is per scope: a "no" about the PC does not silence a question
about Roblox. A grant is not a lock — minutes that have themselves run out can be asked about
again — and the daily cap still stands behind all of it.

If a grant lands during a sign-out countdown, the ordinary path cancels it: the rule stops
blocking, `SessionLockoutService` dismisses the warning and clears the schedule. That is the same
path that ends a manual block for the granted minutes — nothing separate lifts it. **Do not add a
path that grants time locally** — offline, the cached snapshot is the answer, and a request simply
waits for the next synchronization, which is also why the window is anchored to the decision and
not to the moment the PC hears about it: a grant nobody was there to use is over rather than
waiting to be spent.

### Rule precedence

A PC or application is unavailable when any relevant rule denies it, in this order:

1. active manual block;
2. active-time daily total greater than or equal to the limit;
3. current device-local time outside the weekly schedule.

Extra time a parent granted while the scope was shut suspends the first and the third for the
minutes it was given, counted from the decision; it never suspends the second, which the same
grant has already raised by those minutes.

PC rules are evaluated before the interactive desktop is exposed. Application rules are evaluated at
process discovery and again while the application is foreground, so reaching a limit closes an
already-running application too. Overnight schedule windows are evaluated against both the current
and the previous weekday.

### Active time and the trusted clock

`SessionAgent` samples the foreground window/process and `GetLastInputInfo` every two seconds.
`ControlService` applies the configured idle threshold and counts only accepted foreground samples,
so PC usage is always lower than elapsed login time.

**An application is also in use when it is heard rather than seen.** A child in a Discord or
Telegram call behind a game is using the call application the whole time, and the foreground
window never shows it. Each sample therefore also carries `AudibleApplications`: what
`AudioSessionDetector` finds holding an active Windows audio session on any active endpoint (a
headset set as the communications device is where calls live). A session owned by a helper the
catalog refuses - WebView2 behind Teams or WhatsApp - is walked up to the process that started it.
The service re-checks every entry through `ApplicationCatalogPolicy` exactly like the foreground
application, and counts it toward **that application's own time only**, once per sample however
many ways it was seen:

- holding the microphone (a capture session) is a call and counts even while the keyboard is idle;
- merely playing sound counts only while somebody is at the PC, so a game left humming in its menu
  does not spend its limit on an empty room.

PC time keeps the input-based idle rule, so an application's total can exceed the PC's on a day
spent on a call. Background applications get the 15-, 5-, and 2-minute reminders too; closing one
needs nothing new, because `ProcessMonitor` already evaluates every running application.

`EnforcementCoordinator` is the only writer of local usage, so it keeps the running totals in
memory and writes them back at most every ten seconds, before a usage batch is cut, and while the
service is stopping. Enforcement always reads the in-memory total, so a limit is still exact to the
second; a hard crash can lose at most the seconds since the last flush. **Do not read usage
straight from `LocalStore` in an evaluation path** - it will miss the buffered seconds.

Durations come from `Stopwatch` monotonic elapsed milliseconds, never from subtracting wall-clock
timestamps. A single sample delta is capped at 30 seconds so suspend/resume or a stalled client
cannot create a large jump. Fractional seconds stay in memory until a full second can be persisted.

The service anchors its working UTC clock to monotonic time, and every successful sync replaces that
anchor with `ServerUtcNow`, so an ordinary wall-clock edit during the running service cannot shift
schedules or reset a daily limit. Device-local dates and weekdays come from the configured Windows
timezone. **Use `TrustedClock` in enforcement paths, not `DateTimeOffset.UtcNow`.** A LocalSystem
service restart while completely offline necessarily begins from the machine clock; defending that
boundary robustly would need a trusted external clock or stronger anti-tamper, and is documented
rather than hidden.

### Offline storage and synchronization

SQLite uses WAL, `synchronous=FULL`, and transactions. `LocalStore` holds the last validated rule
snapshot and revision, per-local-date PC and application totals, pending usage totals, discovered
application descriptors and their sync state, and durable idempotent usage batches.

**Offline never means allow.** The cached snapshot keeps being enforced when the server is
unreachable, and the service never falls back to permitting everything. New usage accumulates
locally; after reconnection the agent uploads discovered applications, uploads durable usage
batches, refreshes rules, acknowledges commands, and resumes SignalR.

**Usage accounting is idempotent.** Pending counters move into a batch and zero in the same
transaction. The server records every batch ID (`ProcessedUsageBatch`) before acknowledging, so an
uncertain retry cannot double-count, and the agent deletes a batch only after a successful response.
Do not add a path that counts time outside this pipeline.

### Application identity and the catalog

`ApplicationIdentity.CreateKey` builds a stable key, preferring in order:

1. MSIX package family identity;
2. signature publisher plus product and executable name;
3. signature publisher plus original filename when product metadata is absent;
4. product plus original filename;
5. normalized executable path and filename as a last resort.

File version and SHA-256 may be recorded for diagnosis but do not participate in a signed
application's stable key, so normal updates can move paths, change hashes, and change versions
without losing the rule. **Changing this precedence breaks existing rules.** Matching also uses a
weighted publisher/product/original-name score for diagnostics and migration.

The management catalog includes interactive Win32 and MSIX applications such as browsers, Notepad,
Xbox, Calculator, and Photos. When Windows places a packaged app inside `ApplicationFrameHost`,
SessionAgent resolves the packaged child process before creating its identity.
`ApplicationCatalogPolicy` excludes Windows operating-system processes, services, helper packages,
compatibility components, runtimes, updaters, installers, and KidTime itself. Foreground PC time
still counts when a non-manageable shell surface is active, but those components never get
application cards or rules.

`ApplicationCatalogPolicy.ResolvePrincipal` runs first in both `IsUserManageable` and
`NormalizeForCatalog`, so discovery, enforcement, and the parent's catalog cannot disagree about
which application a process belongs to. It rewrites a **satellite that carries the application's
own window** onto that application: Steam draws its window from `steamwebhelper.exe` in a nested
CEF directory, so without this the child's Steam time lands on a helper nobody recognizes, or — once
helpers are filtered — on nothing at all. Renaming it onto `steam.exe` produces the same identity
key, which is also what makes blocking Steam close the window the child is looking at. **Only
satellites with an interactive window belong there.** A crash handler or an updater is excluded
outright, because it running is not the child using the application. Four filters carry most of
that weight:

- a role word ending the executable name, with or without a separator — `steamwebhelper` is one
  word to Steam and a helper to everybody else — and a `(2)` copy marker and a trailing bitness
  marker stripped first, so a repeat download is still the installer it is and `nvsphelper64.exe`
  is the helper `nvsphelper` is. `agent`, `service`, and `services` are role words too:
  `lghub_agent.exe` and `BlueStacksServices.exe` run whether or not a child ever opens Logitech
  G HUB or BlueStacks. So are the abbreviations and the other background shapes a vendor writes
  into a file name — `svc`, `daemon`, `watchdog`, `tray`, `proxy` — and the probes an application
  runs against the machine, `driverquery`, `sysinfo`, and `adb`: `BstkSVC.exe`, `vgtray.exe`,
  `lghub_system_tray.exe`, `mscopilot_proxy.exe`, `vulkandriverquery64.exe`, `steamsysinfo.exe`
  and `HD-Adb.exe` all earned a card on a real controlled PC and none of them is a window a child
  opens. `InteractiveDespiteRoleName` is the narrow exception, and Riot is why it exists — the
  window a child signs in and launches games from is `RiotClientServices.exe`;
- a role word **leading** the name, for the same reason: Rockstar's
  `uninstallRGSCRedistributable.exe` reached a parent's panel as "Rockstar Games SDK", and
  `unins000.exe` is what every Inno Setup package leaves behind;
- `crashhandler`, `crashreporter`, `crashpad`, and `errorreporter` anywhere in the name, which is
  what `UnityCrashHandler64.exe` and Steam's reporters are;
- `wextract.exe` as the original filename: an IExpress self-extractor keeps that version resource,
  which is why KidTime's own `KidTimeSetup.exe` once announced itself to the parent as "Internet
  Explorer";
- a dotted version number inside the executable name, which is the shape of a download rather than
  of an installed application: `ProtonVPN_v5.1.7_x64.exe` is the installer a child ran once, and
  the application it installed is plain `ProtonVPN.exe` and keeps its card. A game whose name
  merely ends in a digit — `cs2.exe` — carries no version and is untouched;
- `hypervisor` and `installer` in the product or display name, and the bare console tools an
  application ships and starts on its own (`adb.exe`, `ffmpeg.exe`). Those two carry no version
  metadata at all, so they reached the panel as cards named after the executable;
- a package family read back out of a `WindowsApps\Name_Version_Arch__PublisherId` path when the
  process did not report one. A family only inferred this way still faces the executable checks;
  a reported one identifies the application outright.

A path can be the stable fact where a name is not: `\NvBackend\` is NVIDIA's background
directory, whose telemetry and cache processes all report the component's product name and get
renamed between driver versions - `OAWrapper.exe` became `NvOAWrapperCache.exe` on one controlled
PC inside a week. Nothing a child opens lives there.

**The executable checks run ahead of that last short-circuit**, or a runtime host is taken at its
word about the application it is hosting: WhatsApp draws itself through WebView2, so
`msedgewebview2.exe` reports WhatsApp's package family and earned a card named "Microsoft Edge
WebView2" — neither a name a parent recognizes nor a thing blocking WhatsApp goes through.

Because the panel and the reconciler both filter through `IsUserManageable`, widening it also
retires entries already in the database, and `ApplicationCatalogReconciler` merges a satellite's
existing rules and usage into its principal on the next server start.

#### Naming a packaged application

`Get-AppxPackage` answers with the package identity, so the first real controlled PC filled its
applications page with `Microsoft.BingNews` and `38833FF26BA1D.UnigramPreview`. The manifest
carries the name Windows itself shows, and `MsixPackageReader` reads it: the display name is
usually an indirect `ms-resource:` reference whose string lives in the package's compiled
resources, resolved through `SHLoadIndirectString`. Several wrappings are tried in turn because
none covers everything — the package's own `resources.pri` on disk, which is what can work from
LocalSystem where none of these packages is registered to the caller, and the expanded
`ms-resource://Name/Resources/Key` form, which is what Calculator, Terminal, and Photos need.

The same manifest is what separates an application from a shell component. A packaged component is
a packaged application by every mechanical test — same publisher, same install root, same shape of
name — so no name filter tells the widget feed or the handwriting dictionary from Photos.
`AppListEntry="none"`, or no `<Application>` at all, does: it is Windows' own statement that the
package has no Start entry and is not something a person opens. On a real PC that keeps 18
applications out of 111 packages. A manifest that cannot be read keeps its package, named as
before; losing a real application because one file would not open is the worse failure.

#### Windows' own applications

A real controlled PC carries three dozen in-box packages that pass every catalog test — Clock,
Weather, Feedback Hub, Quick Assist — and listing them first buries Steam, Roblox and Discord under
things nobody sets a rule on. The applications page therefore hides them behind a **Show Microsoft
apps** switch, off by default and remembered per browser.

`ApplicationCatalogPolicy.IsMicrosoftPublished` makes the call on the server, because only the
server sees the publisher and the package family. Two rules keep it from hiding the wrong thing:
an application that already carries a rule is never hidden — losing sight of a limit you set is a
worse failure than a long list — and `MicrosoftEntertainmentPackages` exempts the games Microsoft
publishes. Minecraft is why that list exists: its family is `Microsoft.MinecraftUWP_8wekyb3d8bbwe`,
so a switch filtering on the publisher alone would hide the one application a parent most wants a
rule on. **This is presentation only.** A hidden application is still in the snapshot, still
evaluated, and still enforced.

`ApplicationCatalogPolicy.GetFriendlyDisplayName` is the second line, and it runs on the server, so
it repairs rows an older agent already uploaded. A display name that is the package's own identity
is rewritten to the leaf of that identity with its words split — `Microsoft.BingNews` reads "Bing
News", `NVIDIACorp.NVIDIAControlPanel` reads "NVIDIA Control Panel". `PackageDisplayNames` overrides
the handful where that would be confidently wrong: Media Player is not "Zune Music" and Clock is not
"Windows Alarms". A name with a space in it was written by a person or read from a manifest, and is
never rewritten.

### Secrets, enrollment, and removal

- Parent passwords use ASP.NET Core's versioned `PasswordHasher` format.
- Parent API tokens are signed with a random, installation-specific HMAC key and expire after 12
  hours.
- The browser never exposes the JWT to client JavaScript; Next.js stores it in an HTTP-only, strict
  same-site cookie and proxies API calls.
- Enrollment tokens are random, single-use, short-lived, and stored server-side only as SHA-256
  digests. Device credentials are independent random values, also stored only as digests.
- On Windows the raw device credential is DPAPI LocalMachine protected, and its file ACL permits
  only LocalSystem and administrators.
- ASP.NET Data Protection keys persist in a dedicated Docker volume, writable only by the non-root
  application UID and encrypted with the mounted HTTPS certificate.
- **Logs intentionally omit** passwords, JWTs, device tokens, enrollment tokens, and certificate
  passwords.

Enrollment is driven from the signed-in web panel. The code contains only the one-time enrollment
token and the server certificate fingerprint, optionally wrapped with the public server URL as a
single setup code; it carries no parent credential, no device credential, and no rule data, so an
intercepted code can do nothing beyond enrolling one PC before it expires or is redeemed. The setup
executable is downloadable only by an authenticated parent, is served with its published version,
byte size, and SHA-256 digest, and embeds the same agent package the automatic updater verifies.
Setup requires normal Windows administrator elevation, refuses to enroll a PC that already holds a
device credential, and offers only enabled non-administrator local accounts. `ControlService` has a
hidden `enroll` subcommand (`EnrollmentCommand`) that setup shells out to before the service starts.

Removal has two independent paths. The web panel permanently deletes a device's rules, usage,
application associations, enrollment record, and credentials — revoking agent API access, but
deliberately not pretending to uninstall software on a possibly offline PC. Local removal starts in
the controlled user's screen-time window, requires fresh parent email/password verification against
the server, and uses the returned parent JWT only inside the LocalSystem service to delete the
matching device; a protected LocalSystem helper then stops and deletes the service and removes the
fixed `Program Files\KidTime` and `ProgramData\KidTime` directories. It is unavailable offline, and
remains possible after web-only deletion because parent authentication is independent of the device
credential and an already-absent server device is treated as removed.

The self-signed certificate from `scripts/initialize-server.sh` is pinned by SHA-256 on the agent.
The agent applies ordinary chain and hostname validation first and consults the pin only when that
fails, which is what makes a direct LAN deployment trustworthy without a public CA. Publishing the
agent API behind a reverse proxy with a publicly trusted certificate therefore survives ordinary
renewals: the renewed certificate still validates by chain and the stale pin is never reached.
Replacing the certificate on a deployment that actually depends on the pin does require re-enrolling
or re-pinning. The private key lives only on the Docker host in the directory mounted read-only at
`/https`; the file is world-readable so the non-root container can load it, and the PKCS#12 password
is held in the untracked environment file.

### PC blocking, notifications, and anti-tamper

When a PC rule blocks access, the LocalSystem service queues a final warning stating the reason,
usage where applicable, and the next available time - drawn as the countdown card, or as a tagged
native Windows notification when the card cannot be drawn. The service owns the monotonic deadline
and then calls `WTSLogoffSession`; **notification delivery is never trusted for enforcement.** The
warning carries an explicit expiration, and the card cannot strand itself either - the constraints
below are what guarantee that.

**The warning ends where the screen time does, rather than starting there.** A schedule closing at
22:00 warns the child at 21:59 and signs the session out on the hour; warning on the hour and
signing out at 22:01 gives away a minute the rule did not. `RuleEvaluator.FindPendingDeviceRestriction`
is what makes that possible: it names the restriction the PC is heading into and the seconds left
before it starts. Two of those deadlines are wall-clock facts - the schedule window closing, and
the window a grant opened over a block running out. The third, the daily limit, counts down in
active foreground time, so the coordinator says whether the child is actually spending it and the
limit is a deadline only while they are; a PC left idle at four minutes remaining is not about to
close, and predicting that it is would sign out a PC nobody was using. When a standing warning's
deadline moves more than ten seconds - a parent granting time, a child stopping short - the card is
withdrawn and, if something is still closing, redrawn against the new one.

How long the warning lasts is decided by whether the child was there to be warned. A restriction
that arrived while they were signed in - a parent locking the PC from the panel, which has no
deadline to count down to - is worth the full 60 seconds. **Finding one already in force on the way
in is worth 20**, because there is no work in progress to save and the PC is meant to be shut: that
covers signing in during a closed window, signing in again after a forced sign-out, and a service
restart, which cannot know what came before it. The 60-second grace is also spent once per
restriction episode and persisted locally, so a restart mid-warning cannot hand out another.

**An enforcement action is granted for one attempt and spent when it is issued, never latched onto
what it acted on.** `PcSignOutSchedule` and `ApplicationBlockLeases` both encode that. A session
still signed in `PcSignOutSchedule.SettlePeriod` after its sign-out - the call does not wait for the
session to end, so a few seconds is ordinary teardown - starts the warning cycle over and reports
an error the parent sees, rather than being taken as already handled. `ProcessMonitor` likewise
retires an application's lease as it issues the close: keeping it until a sweep saw nothing of that
application running let a relaunch inside the same two-second window inherit a spent lease and run
unrestricted for the rest of the restriction episode. If the parent dismisses the
restriction during the countdown, a keyed dismissal closes the card, hides any toast, removes it
from Notification Center, and cancels sign-out. When the rule
ends, a normal notification says the PC is available and repeats the prior reason.

The tray window is a four-panel view - Today, Apps, Connection, About - switched by a button strip
rather than a `TabControl`, because only the visible panel then stays in the visual tree. A fifth,
Internet, appears only where a DNS filter is configured. About carries the installed version, the
privacy summary in the child's own words, and the removal flow.

#### Opening that window

A tray icon is not a way to find an application. Windows hides icons behind the overflow chevron by
default, so a child who was never shown where it is cannot reach their own screen time, the Apps
tab, the Internet tab, or the button that asks a parent for more minutes. `AgentShortcuts` therefore
writes **KidTime** into the all-users Start menu and onto the all-users desktop, which is what makes
it an application the child can find by name in Windows Search rather than a thing that only happens
to them. The executable carries the icon (`ApplicationIcon` on the SessionAgent project), so the
shortcut, the taskbar, Alt+Tab and Search all show the same mark.

Two decisions there are load-bearing:

- **A shortcut must never start the agent.** The service launches exactly one copy and the named
  pipe accepts only that copy's process id, so a second one would take the single-instance mutex,
  be refused by the pipe, and leave the supervised agent unable to start at all - a child with no
  tray icon, no window and no notifications while enforcement carried on without them. So the
  shortcuts pass `SessionAgentArguments.Show` and `AgentActivation` signals the running agent
  through a session-scoped named event and exits. The signal carries **nothing**: its whole
  vocabulary is "open the window", which the tray icon already does, so this is not a second way
  into the enforcement boundary. That boundary is the pipe. The argument and its reading live in
  the domain because both halves must agree and neither project references the other; the reading
  is deliberately strict, since treating the service's own argument-less launch as a shortcut is
  the failure that costs the child the agent entirely.
- **The service writes them, not setup.** Setup only ever runs on a PC that is not yet enrolled, so
  an installation that already exists would never get them, while the service starts after every
  update and every reboot. That also makes them self-healing. Both folders are all-users ones,
  readable and not writable by ordinary users, so the child keeps the shortcut and cannot quietly
  delete it - and local removal takes both away, because an icon pointing at nothing is not the
  PC returned to how it was. The name is `KidTime` in every language: it is a product name rather
  than a sentence, and a localized file name would have to be deleted and rewritten every time a
  parent changed the PC's language through an ordinary rule revision.

A shortcut still has to be clicked, and a child who has never opened the window does not know
there is anything in it. So a parent can ask for it to **open by itself when the child signs in**:
`DeviceRule.OpenWindowAtSignIn` travels inside `DeviceRuleSnapshot` exactly as the language does,
so switching it on bumps the revision and lands over the ordinary sync path. It is off by default,
and it enforces nothing - the window can be closed straight away and no rule reads it.

Two constraints make it once rather than often:

- **The service decides, not the agent.** The agent cannot tell a sign-in from its own relaunch,
  and the supervisor starts it again within two seconds of it dying - so an agent that opened its
  window on startup would turn a crash loop into a window reopening every two seconds on a child
  who can do nothing about it. `SessionAgentSupervisor` therefore tells
  `EnforcementCoordinator.NoteAgentLaunchedAsync` which session it launched into, and the
  coordinator answers once per sign-in.
- **The answer is persisted, and a sign-in is not a session id.** `LocalStore` spends it like the
  first-block grace, so a service restart mid-afternoon - which is what every automatic update
  is - cannot open a window over what the child is doing. The key is the boot the session belongs
  to as well as its id, because Windows hands the same id out again after a restart and that
  restart is exactly the sign-in the window should open for.

It reaches the agent as `EnforcementState.ShowWindow`, a single bit spent by the service as it is
sent. That is deliberately the same vocabulary as the shortcut's signal in the other direction: it
says "open the window" and can say nothing else, so **do not grow it into a channel for the server
to drive the child's desktop**.

Notifications are native Windows toasts. SessionAgent emits them marked with the supported urgent
scenario, high priority, and explicit reminder audio — the Windows-supported way to break through
Focus Assist without changing the user's global setting. Its tray dashboard composes maintained WPF
UI 4.3.0 controls (FluentWindow, Card, ProgressRing, InfoBar, Badge, SymbolIcon, menu, tray),
follows the Windows theme and accent, and receives status through the process-validated pipe. It
cannot edit or bypass rules; its only privileged action is the separately parent-authenticated
removal flow. Do not build a custom widget toolkit here.

The one custom window is `CountdownCardWindow`, and it exists for the one thing a toast cannot do:
show the seconds actually draining before a forced sign-out or close. It **is** the final warning,
and the urgent toast is its fallback: two warnings for one deadline only competed for the same
corner. It is not a blocker. The constraints are the design:

- It is drawn only for a notification that is both urgent and carries `CountdownSeconds`.
- `CountdownCard.Show` reports whether a card is genuinely on screen, and the agent raises the
  native urgent toast when it is not. **A card is best-effort; the warning is not.** Never make the
  card the only path without keeping that fallback.
- The card carries its own reminder sound, because it replaced a toast that had one and a silent
  warning is easy to miss under headphones. Focus Assist silences notifications, not an
  application's own audio. A machine set to No Sounds simply stays quiet - which is why the
  countdown never depends on the sound either.
- The service still owns the monotonic deadline and signs out or closes the application whether a
  card was drawn or not. A card that fails to appear is reported as a warning and falls back.
- `WS_EX_NOACTIVATE` and `WS_EX_TOOLWINDOW` keep it out of the focus chain and out of Alt+Tab. A
  window that stole the keyboard while telling a child to save their work would be self-defeating.
- It is a small corner card sized like a toast, cannot be resized, and dismissing it hides the
  card only. **"Got it" carries the accent and the extra-time shortcut is drawn small and quiet
  beside it**: this card is a warning, and acknowledging it is the ordinary thing to do, while
  asking for more time is the side door that is not always even offered. The caption sits on its
  own row above the two, because both labels are sentences in Russian and on a 380-wide card a
  caption plus two buttons ran straight through each other. A warning whose buttons overlap looks
  broken at the one moment it must not.
- It cannot strand itself. One shared one-second timer closes any card past its deadline and stops
  when the last card goes; and the window belongs to the supervised SessionAgent process, which
  the service restarts within two seconds if it dies, so the card dies with it.

**Do not grow this into a blocker, a full-screen overlay, or a second notification system.** If a
message can wait, it is an ordinary toast; if it cannot, it is this card, with the urgent toast
behind it for the case where the card cannot be drawn.

`UserNotification.IsUrgent` decides the toast scenario: it stays on screen and overrides Focus
Assist. Two things are urgent — **the final warning before a forced sign-out or close, and the
15-, 5-, and 2-minute reminders**. The reminders earned it: a child absorbed in a game never sees
an ordinary toast fade, and a limit warning nobody reads is the same as no warning. They stay
toasts all the same, because nothing is closing yet: only `CountdownSeconds` selects the card, and
a reminder has none. What a reminder does carry is one `PersistentNotificationKey` per restriction
(`reminder:pc`, `reminder:app:<identity>`), so 5 minutes replaces 15 instead of stacking beside
it — an urgent toast stays on screen until it is dismissed, and three of them in the corner is its
own way of not being read. The final warning carries both a countdown and a key, so in ordinary
operation it is drawn as a card and its urgent toast is never seen. Rule changes, availability, and
a completed update are ordinary toasts with default priority. Urgent toasts carry two short lines
and nothing else: the title states what is closing and how long is left, the body states the reason
and to save work now. Detail belongs in the screen-time window; an interruption a child has seconds
to read must not be a paragraph.

**A queued warning states the time left when it reaches the child, not when it was queued.** The
service hands the agent every waiting message on one exchange (`EnforcementState.Notifications`),
and `DrainNotifications` rewrites each final warning — its `CountdownSeconds` *and* its title,
which carries the same number — from the monotonic deadline the caller started. Handing out one
message per two-second sample and keeping the seconds a warning was born with is how a child came
to watch a card count five seconds down as the application closed: the card counted from when it
appeared while the service counted from when it was queued. A warning whose deadline has already
passed is dropped rather than drawn at zero, and logged. **When adding a notification that carries
a deadline, queue it through `EnqueueCountdown` with a builder, never as a finished
`UserNotification`** — a fixed string cannot be restated.

PC time-limit and schedule revisions queue a normal notification regardless of the foreground app;
application revisions queue one only if that application is currently open. The final forced-close
warning is a tagged, long-duration notification with a deadline-based expiration: 60 seconds for the
first blocked launch in a restriction episode, 20 seconds for later launches in the same episode,
persisted across restarts. If the application exits before its countdown ends, a keyed dismissal
hides the notification immediately.

The LocalSystem service restarts SessionAgent every two seconds if it exits. The install directory
is read/execute-only for ordinary users, service data is reachable only by LocalSystem and
administrators, the service control ACL denies stop/configure rights to interactive users, service
recovery is enabled, and the SessionAgent process DACL prevents a Standard User from terminating or
injecting into it.

**A session that is going away is not a crash loop.** `WTSLogoffSession` does not wait, and for
several seconds afterwards the session still exists and still answers for its user while everything
in it is being torn down - so an agent launched into it dies immediately, five times in a row, and
the supervisor used to report that to the parent as a fault on a PC doing exactly what it was told.
Every forced sign-out produced one. `SessionAgentSupervisor` therefore neither launches into nor
counts a session that `WindowsSession.IsSessionActive` says is not running its desktop, or one this
service has just signed out (`PcSignOutState`, a shorter window than `PcSignOutSchedule.SettlePeriod`
so a sign-out that never takes effect does not cost the child their tray agent for as long as the
failure lasts). A session that cannot be asked counts as active: a child with an interface and a
false report is a better failure than a child with none. **The child ending the session
themselves looks the same** - signing out, restarting, or shutting down - and the service is told
nothing, so the agent says it: WPF's `SessionEnding` makes it exit with
`SessionAgentExitCodes.SessionEnded`, and the supervisor stands down until the session loses its
user (at most a minute, so a cancelled shutdown gets its tray agent back). **A sign-out can also
take longer than both windows** - a slow PC saving a game or unloading a profile has been seen
spending over thirty seconds on it - and then a copy launched into it cannot even start: Windows
refuses it with `0xC000026B` (`STATUS_DLL_INIT_FAILED_LOGOFF`, the window station is shutting
down). The supervisor reads that exit code as the session ending, stands down exactly as for
`SessionEnded`, and records it in `PcSignOutState`, where `SessionLockoutService` finds it and
gives the session another settle period instead of reporting "still signed in after it was signed
out" and warning a child who is no longer there. Without that word from Windows, a session that
outlives `SettlePeriod` is still reported and warned again. When the loop is real,
the error carries the agent's exit code - read from the handle `CreateProcessAsUser` returned,
because an agent that dies within milliseconds is gone before it can be reopened by id -
and `0xE0434352` is a .NET exception that escaped, which is the difference between the agent
crashing and something killing it.

### Automatic agent updates

Releases are served only to enrolled device credentials over the same certificate-pinned HTTPS
channel used for rules and usage. Each manifest carries version, exact byte size, and SHA-256; both
server and agent verify the package before installation. `AgentUpdateWorker` checks periodically
(five minutes by default), stages under `C:\ProgramData\KidTime\updates` outside the install
directory, keeps a rollback copy, replaces only service binaries, and restarts `KidTimeControl`.
The updater script records its outcome, so the first run after a restart reads that file and either
queues one ordinary "KidTime updated" notification for the child or logs an error - which the
parent then sees in the error log - when the update failed and was rolled back.
Enrollment credentials, cached rules, usage, and logs stay in ProgramData. The API compares the
heartbeat-reported assembly version with the published manifest and exposes current, outdated,
downloading, installing, or failed state to the parent UI.

### Web filtering (Technitium DNS)

A household that governs a PC also governs what that PC can reach, and the two are not the same
job. **KidTime does not filter the web and must not start.** Filtering is DNS-level, covers every
device on the network at once, and is already solved by the
[Technitium DNS Companion](https://github.com/fail-safe/technitium-dns-companion) the household
runs. What KidTime adds is the part neither of those two does: telling the child what is in force,
in the window they already have open, in their own language.

So the whole integration is **read-only, one-way, and optional**. It is configured in the `Dns`
section (`Dns__ApiUrl`, `Dns__Username`, `Dns__Password`, and optionally `Dns__ConsoleUrl`,
`Dns__NodeId`, `Dns__GroupName`, `Dns__PinnedCertificateSha256`, `Dns__AllowInvalidCertificate`,
`Dns__RefreshSeconds`); with `Dns__ApiUrl` empty the feature is absent from the panel and the child's
PC alike, which is the case for most installations.

**How the server reaches it matters as much as the credentials.** The companion is normally a
second Compose stack on the same host, and pointing `Dns__ApiUrl` at the household's public
address makes a local read leave the machine, come back through the reverse proxy, and need an
admin port opened to a container - each of those can be closed for good reasons by somebody who
will never connect the silence to KidTime, and the symptom is a twenty-second connect timeout
rather than an error. `KIDTIME_DNS_NETWORK` and `KIDTIME_DNS_EXTERNAL` in `compose.yaml` put the
server container on the companion's own Docker network instead, so `Dns__ApiUrl` can be its
container name and port. Unset, they leave an ordinary empty network and change nothing, because
most installations have no DNS server to reach.

1. `TechnitiumCompanionClient` signs in once with the configured credentials and keeps the session
   cookie the companion issues, renewing it silently when it is refused. Every other call is a GET:
   `advanced-blocking/{node}`, `nodes/dns-schedules/rules`, and the domain groups.
   **Do not add a write.** A second editor for the same setting is a second way for it to be wrong,
   and the panel's button already puts the parent in the console that owns it. Ordinary chain and
   hostname validation runs first and the configured SHA-256 pin is consulted only when it fails -
   the same order the Windows agent uses against this server, which is what makes the companion's
   self-signed certificate usable without trusting everything.

   **"Refused" has two shapes here and only one of them is a 401.** The companion is a front for
   the DNS node: it signs in to that node on our login and keeps the node's token in the session,
   and a single moment of the node being unreachable is enough for the node to reject it
   afterwards. The companion then drops the token and needs a fresh login to mint another - but
   until it gets one it goes on answering **200**, because the request to the companion itself
   succeeded, and simply leaves the node's half of the payload out. Read at face value that is a
   household with no block lists and no site groups, and a filtered home was told for hours that
   its filter was off, with a fresh timestamp and nothing in the log. So a 200 that carries no
   `config` renews the session exactly as a 401 does, and if it comes back empty a second time the
   read **fails** rather than answers.

   That is the general rule here, and it is the whole reason this client throws: **a read that
   half succeeded is a failed read.** Nothing may be defaulted to empty - not the schedules, not
   the domain groups - because the caller cannot tell a configuration it could not fetch from one
   that blocks nothing, and only one of those two is worth saying out loud.
2. `DnsFilteringService` caches one normalized `DnsFilteringSnapshot` for `RefreshSeconds`
   (120 by default). **Synchronization must never wait on a third-party service**, so the sync reads
   a cache rather than making a call. A read that fails after an earlier success keeps that answer
   whole - its state, its contents and its timestamp - and only sets `IsStale`, so the panel and the
   child's tab go on describing the filtering that is in force instead of going blank; that is the
   same shape as cached rules being enforced offline, and it is why staleness is a flag rather than a
   state. `Unreachable` is only for a configuration that has never been read at all. A failure is
   retried after 30 seconds rather than on the next sync. **`Inactive` is the DNS server saying it
   blocks nothing for this household, never KidTime failing to ask** - every other way of reaching
   it, a group the configured `Dns__GroupName` does not name included, is logged rather than shown
   silently as a filter that is off.
3. `DnsFilterPolicy` in the domain turns the configuration into the two things a person can read.
   `Summarize` folds block lists into named categories, narrowest evidence first: an AdGuard
   registry number, then the file name, then the rest of the path, and **never the host** - AdGuard
   serves every list it registers from `adguardteam.github.io`, so reading the publisher labelled a
   real household's gambling, adult and tracker lists all as "Ads". HaGeZi's malware list is called
   `tif.txt` and is served out of a directory called `adblock`, which is why the file name outweighs
   the directory. An address that says nothing is `Other` rather than guessed at.
   `Evaluate` merges the schedule windows and answers whether a set of sites is closed now and when
   that turns over, in the timezone the rule was written in.
4. The snapshot rides on `AgentSyncResponse.DnsFiltering`, **beside the rules and not inside them**.
   It is not a rule: KidTime enforces none of it, so it bumps no revision and queues no command, and
   `EnforcementCoordinator` holds it only to hand to the status snapshot. **`RuleEvaluator` never
   sees it.** `LocalStore` caches it in `dns_cache` so an offline PC still has the tab, with its age
   attached.
5. The child's window grows an **Internet** tab: whether filtering is on, the categories blocked at
   every hour, and each named set of sites with the time it comes back. It has no controls at all,
   because nothing on it is KidTime's to change. Where nothing is configured the tab button is not
   drawn - an empty tab would suggest something is being done that is not.
6. The panel's **Web filtering** page is a status line and a button to the DNS console, and that is
   deliberately all it is.
7. When a site the household filters does not open, the child is told **why**, once. This is the
   one place the snapshot is read for something other than drawing a tab, and it still decides
   nothing: no rule is evaluated and nothing is blocked or allowed - the only outcome is a
   sentence. See [Explaining a page that did not open](#explaining-a-page-that-did-not-open).

Which filtering group a household falls in is **named in configuration, not inferred**. The
companion maps groups to networks, but the address the KidTime server sees is whatever the last
proxy hop presents rather than the PC's own, so guessing would silently describe the wrong rules.
`Dns__GroupName` names it; a DNS server with one group needs no name.

#### Explaining a page that did not open

A child whose home network refuses a site sees only their browser's own error page and is told
nothing, so the household's filtering reads to them as the computer being broken. KidTime already
holds the rules that explain it, and saying so is the one thing neither the DNS server nor the
browser will do.

**It explains the rule and never names the site**, and that is a constraint rather than a
shortcoming. The address the child typed is browsing history under another name; reading it would
need a browser extension, the address bar over UI Automation, or the DNS query log, and all three
are on the far side of [the privacy boundary](#privacy-boundary). So the message describes what the
household blocks and lets the child draw the connection, which is also the answer they actually
needed.

1. `BrowserPageErrorDetector` runs in the child's own session, on the foreground window title the
   agent already samples, and answers one of four values: no error, or one of the three shapes a
   DNS refusal takes, which is decided by how the household's DNS server was told to answer a
   blocked name. `NXDOMAIN` gives a name that did not resolve; `0.0.0.0` gives a connection that
   did not open; **an address of the DNS server's own** - the commonest setting, and the one that
   serves a block page - gives a security failure, because that block page cannot present a
   certificate for the site whose name was asked for. The third was missed at first, and a real
   household saw nothing at all. **The title never leaves the session**; only that value rides on
   `SessionUsageSample.BrowserPage`, which is inert data exactly like a fault report and widens the
   pipe's privileges not at all.
2. Only Gecko browsers are read, and only because **Firefox titles its error page with a sentence**
   rather than with the host. Chromium puts the hostname in the title, so recognizing it there
   would mean reading the address - which is why no Chromium browser is in `SupportedBrowsers` and
   adding one is not a fix. A browser whose interface is in a third language simply produces no
   match, costing the child an explanation and nothing else.

   Two things about that sentence are worth knowing before touching the table. Only a **document**
   title can appear in a window title: Firefox draws a second, more specific heading in the body
   of the page ("Unable to connect", "Secure Connection Failed") that never reaches the title bar,
   so adding one of those looks right and matches nothing. And there are **two generations of the
   error page**, the newer behind a pref, so both wordings are in the wild and both are listed -
   the newer card titles every network failure `neterror-page-title` (a name that did not resolve
   included, where the older page used `neterror-dns-not-found-title`) and every security failure
   `fp-certerror-page-title`. The ids, not the English, are what the table is checked against;
   Firefox ships every translation in its own `omni.ja` and langpacks, which is where a new
   language's strings come from rather than from a translator.
3. `EnforcementCoordinator.QueueWebFilterExplanation` decides. It is silent unless all three hold:
   the page **just** failed (a transition, so an explanation follows the child arriving on the
   error page rather than repeating every two seconds while they read it); the service is reaching
   the KidTime server, because **a home network that is down produces the same error page** and
   blaming the filter for an outage is a confident lie the child cannot check; and
   `DnsFilterPolicy.ExplainRefusal` has something to say, which it does not when filtering is off,
   absent, or never once read. One explanation then stands for ten minutes.
4. `ExplainRefusal` returns the categories blocked at every hour and, of the named sets of sites
   that are shut right now, **the one coming back soonest** - the only one the child can do
   anything about, namely wait for it. A stale snapshot still answers: it is the filtering that
   was in force and almost certainly still is, the same reason cached rules go on being enforced
   offline.
5. It is an **ordinary toast**. Nothing is closing and nothing is counting down, so nothing
   interrupts; `AgentStrings.WebFilterBlockedMessage` composes the whole sentence per language,
   because Russian declines a list of categories after a colon and English does not. **The closing
   line is chosen by how the page failed, and it is the only part that is.** A name that did not
   resolve is also what a typo looks like, so that one asks the child to check the address - told
   only about the filter, they would retype nothing and wait. A security failure is the opposite
   problem: the child is looking at a warning that also appears when a site is genuinely unsafe,
   KidTime cannot tell the two apart, and an explanation that left them readier to click past it
   would have done harm no blocked site is worth. So that one ends by saying not to.

The honest limit: a page that fails because a site is genuinely down, or because the PC's clock is
wrong, looks from outside the browser exactly like one the filter refused, so an explanation can
arrive for a page nothing blocked. That costs one toast stating true things about the household's
rules, which is the cheapest of the failures available - the alternatives all start by reading
where the child went. It is also why neither ending claims that *this* page was blocked.

### Logging

Three processes write to one screen, so they write the same shape. Every line is
`time  LVL  source  message  identifiers`, and the identifiers end with a correlation id that
**joins the panel's line to the API's line for the same click**.

- **Server.** `ServerLogging.AddKidTimeLogging` picks one of two formats and does not compromise
  between them: `KidTimeConsoleFormatter` for a household reading `docker compose logs`, or the
  framework's own JSON console when `Logging__Format=json` ships the output to a collector. The
  formatter drops ASP.NET Core's hosting and activity scopes (`SpanId`, `TraceId`, `ConnectionId`,
  `RequestPath`, `ActionName` and the rest): nothing here is distributed, they repeat what the
  request's own line already says, and together they are longer than any message this server
  writes. It also **refuses to print an exception twice** - Entity Framework formats the whole
  exception into the message it logs it with, and the block underneath buried the line before it.
- **`RequestLogging` writes one line per request, at the end**, because the framework's own costs
  several and still does not say who was asking. It carries the method, the path with any
  secret-looking query value replaced, the status, the duration, and the actor - the parent's
  e-mail, or `device <first eight of the id>`, which is enough to tell four PCs apart without the
  line being mostly a GUID. A 401 or 403 is a warning: on a self-hosted box that is a revoked
  credential or a clock problem, not noise. `/health` and `/hubs/` succeed at Debug.
- **The correlation id** arrives in `X-Request-Id` or is minted, is pushed as a log scope so
  everything inside the request carries it, and **always comes back on the response**, so a parent
  with the browser's network tab open has the string both containers' logs are keyed by. An
  arriving value is bounded and stripped of anything unprintable - it is the one part of this that
  comes from outside.
- **One summary at startup**, on `ApplicationStarted` so Kestrel has bound: version, environment,
  addresses, then the database host, whether a release is published, and whether DNS filtering is
  configured. Half the questions a self-hosted deployment raises are answered by those two lines,
  and expensive to work out afterwards over a chat message. The connection string is reduced to
  host and database; everything else in it is a credential.
- **Panel.** `lib/logger.ts` is the same format in TypeScript and reads the same three settings, so
  one choice in `.env` changes both halves. It is **server-only** and never imported into a client
  component. Its reason for existing is `describeError`: `fetch` rejects with a bare "fetch failed"
  and puts the actual cause in `error.cause`, and the panel used to discard the whole thing and
  tell the parent "KidTime API is unavailable" - which is true of a refused connection, an unknown
  host and a rejected certificate alike, and useful for none of them. `instrumentation.ts` catches
  what escapes a server component, which otherwise reaches the log as a stack trace with no route
  attached.
- **PostgreSQL** is configured in `compose.yaml`. **Statements are never logged wholesale**: that
  is the database's own copy of every rule and every parent's e-mail written to a file nothing else
  protects. What is logged is a statement slower than `KIDTIME_PG_SLOW_QUERY_MS` (500 by default),
  and even then with `log_parameter_max_length=0` - the shape of a slow query is what gets it
  fixed, and its values are the household's data. Checkpoints, lock waits, long autovacuums and
  large temp files are on because each one is rare and each one explains a slow evening.
- **Every container's log is capped** (`x-logging`, 10 MB × 5). A household server runs for months
  and nobody watches its disk; Docker's json-file default is unbounded.
- **The redaction list is the same one the rest of KidTime keeps**: no passwords, JWTs, device
  tokens, enrollment tokens, or certificate passwords, in any of the three. Adding a log line that
  would carry one is the one change here that is not a matter of taste.

### Cost on a slow PC

The controlled PC is often the household's weakest machine, and everything here runs while the
child is using it. Recurring work is kept off the hot path deliberately:

- `SessionAgent` caches an executable's version metadata and Authenticode publisher and rebuilds
  them only when the file on disk changes. Parsing a signature every two seconds was the single
  most expensive thing the agent did.
- The full status snapshot costs the service one rule evaluation per controlled application, so
  the agent asks for it only while the screen-time window is open, plus once every thirty seconds
  to keep the tray tooltip current (`SessionUsageSample.StatusRequested`).
- The window skips rendering when nothing a person can see has changed and updates application
  cards in place instead of replacing the item source.
- The application descriptor is written to SQLite when the foreground application changes or every
  ten minutes, not on every sample, and usage totals are buffered as described above.
- `ProcessMonitor` sweeps every two seconds rather than every second. Blocked applications get a
  20-60 second save period, so a slower sweep changes nothing a child can notice.
- `RuleEvaluator.FindCurrentAllowanceEndUtc` walks the week a minute at a time, and the lockout
  loop needs it every second to warn the child before screen time ends. The answer is an absolute
  instant that only moves when the rules change or when the window it names has passed, so
  `EnforcementCoordinator` keeps it and hands it to `FindPendingDeviceRestriction` and
  `BuildRestriction` rather than each of them scanning again. **The scan belongs to the caller
  that can keep the answer**, which is why it is a parameter there and not a call.

**Prefer removing recurring work over making it faster**, and keep enforcement timing decisions -
sign-out deadlines, close deadlines - on the monotonic clock in the service, where interval changes
cannot move them.

### Privacy boundary

No component contains web filtering, browser hooks, DNS proxying, URL capture, HTTPS interception,
packet inspection, keylogging, screen capture, camera/microphone access, message collection, or
location tracking. Reading which process holds an audio session is not microphone access: no stream
is opened and no audio is read, which is the same fact Windows' own volume mixer and
microphone-in-use indicator show. Foreground window titles travel locally for diagnostics but are not persisted or
uploaded by the current server contract. **Features that would cross this line are out of scope by
design** — do not add them, and do not extend the contract to upload window titles.

Noticing that **a page did not open** does not cross it either, and it is the closest thing here to
the line. The agent reads the foreground window title it already samples, answers one of three
values - no error, a name that did not resolve, a connection that did not open, a connection the
browser refused on security grounds - and sends only that. The title stays in the child's session, no address is parsed out of it, and the explanation
the service then queues describes the household's rules rather than the request. This is why only
Firefox is read: it titles its error page with a sentence, while Chromium titles it with the host,
and taking the host would be taking the URL. **Do not add a Chromium browser to that list**, and do
not reach for the address bar, a browser extension, or a local DNS proxy to make this work
everywhere - each of those is the feature turning into the thing this section forbids.

Reading a DNS server's **configuration** does not cross it, and reading its **query log** would.
KidTime can be pointed at a Technitium DNS server the household already runs and will report what
that server is set up to block; it never asks for, stores, or forwards a single lookup. A tab that
listed the sites a child tried to open would be browsing history under another name, and the
companion's query-log endpoints are therefore deliberately never called. See
[Web filtering](#web-filtering-technitium-dns).

## Conventions

- `Directory.Build.props` sets `TreatWarningsAsErrors=true` for every project and carries the single
  `Version` used by the agent, its update manifest, and the update-comparison logic. A new agent
  release means bumping that version before running `build-agent.ps1`.
- The solution is `KidTime.slnx` (XML solution format), not a `.sln`.
- Domain and server target `net10.0`; ControlService, SessionAgent, and Setup target
  `net10.0-windows` and publish self-contained win-x64.
- `.gitattributes` pins `*.sh` to LF and `*.ps1` to CRLF; keep new scripts on the same side.
- The panel can be served under a path prefix. Anything building a same-origin URL by hand (fetch,
  plain anchors, downloads) must go through `lib/paths.ts#appPath`; `next/link` and `next/navigation`
  prepend it themselves. `NEXT_PUBLIC_BASE_PATH` is baked in at image build time, so changing the
  prefix requires rebuilding the web image.
- A toggle in the panel is a whole row, not a track beside a label: `SwitchField` (a settings row)
  and `SwitchOption` (a compact labelled one) in `components/ui/switch.tsx` render the text *inside*
  `Switch.Root`, so the control is the row and there is one click target rather than a 32×18 pixel
  one. Reach for those instead of the bare `Switch`, and never wrap a switch in a `<label>` to widen
  its target — the label and the control both handle the click and it toggles twice.
- **The panel leads with the weekly schedule, not the daily limit.** A household that governs the
  PC by schedule learns nothing from "No limit" repeated down a page, so a device card headlines
  what the schedule says right now - "Allowed until 21:30", "Allowed from tomorrow 09:00" - and
  draws today as a `ScheduleStrip`: the day as a bar with the allowed windows filled and a marker
  where the PC's own clock stands. The daily limit stays, smaller: its progress bar appears only
  when a limit is set, and an application with no rule shows a dash rather than a sentence about
  a limit it does not have. `DevicesController` computes the schedule state (open now, when it
  closes, when it next opens, today's windows) because only the server knows the device timezone
  well enough. **Those times cross as the device's own wall clock, never as instants**: a Windows
  PC reports a Windows timezone id (`FLE Standard Time`, `Russian Standard Time`), which .NET
  resolves on any platform and JavaScript's `Intl` does not - handed the id, `Intl` throws, the
  panel fell back to its own clock, and a window closing at 21:00 was shown to the parent as
  18:00. So `DevicesController` sends `"2026-08-31T21:00"`, today's date, and the minute of the
  day the PC is standing at, and `lib/schedule.ts` only reads them. Do not convert a timezone in
  the panel.
- **An offline PC has no current application.** `ForegroundApplication` is the last thing that was
  reported, so a card for a device that is not reporting says so instead of naming a game the
  child closed hours ago.
- Every string the controlled user can read lives in `AgentStrings` and must be implemented in
  both `EnglishAgentStrings` and `RussianAgentStrings`. `AgentStringsTests` walks the catalog by
  reflection and fails on a member that returns blank in either language.
- **A XAML-wired event handler must tolerate its siblings not existing yet.** WPF raises
  `ValueChanged` while `InitializeComponent` is still running - applying a `Slider`'s Minimum
  coerces its value off zero - and fields declared later in the markup are still null at that
  point. The exception escapes the window constructor, `AgentApplicationHost` never finishes, and
  the child is left with no tray icon, no screen-time window, and no notifications while
  enforcement carries on without them. Guard the handler; do not rely on attribute order.
- The amount controls at both ends are sliders over the same range, and the range lives in
  `TimeExtensionPolicy` rather than in either UI. `components/ui/slider.tsx` wraps Base UI's parts
  (Root → Control → Track → Indicator + Thumb); the track needs an explicit height because the
  indicator inherits it, and the accessible name belongs on the thumb, not the root.
- A parent-facing list filter that is remembered per browser reads localStorage through
  `useSyncExternalStore`, never through an effect that calls `setState` — the server render cannot
  see localStorage, and the lint rule that forbids the effect is there because the alternative is a
  second render pass fighting the first. `components/application-list.tsx` is the pattern.
- **A page is named for everything on it.** The dashboard carries today *and* the week behind it,
  so it is called Dashboard rather than Today with a seven-day card sitting under the heading.
  Statistics is one PC at a time over 7, 14, or 30 days, and its filters are one row above
  everything they scope, so both charts and every number below read the same slice.
- **Settings earns its tab by telling the parent what to do next.** `lib/suggestions.ts` works the
  list out from what the installation already knows - a PC enforcing nothing because no account was
  chosen, a game that took hours this week with no rule on it, faults waiting, a child waiting on an
  answer - and each one is a sentence and a link to the page that fixes it. **Nothing there changes
  anything by itself**; a settings page that acts on its own suggestions is a settings page nobody
  can predict.
- **Charts are Recharts, in `components/charts/`, and they follow two colour rules.** One series
  wears the brand primary, because a second colour would mean a second thing and there isn't one;
  several wear `--chart-1` to `--chart-6` in the order they are declared, with everything past them
  folded into one `--chart-other` band rather than given a seventh hue nobody can tell from the
  first six. **That order is the safety mechanism, not a preference** - it was validated for
  colour-vision deficiency against the white card, and three of its steps sit under 3:1 there, so
  any chart drawing them also ships the written breakdown beside it (`ApplicationUsageTable` is the
  one for `ApplicationUsageChart`). Gridlines are solid hairlines; only the average and limit
  annotations are dashed, because a dashed line should mean a threshold.
- **A chart's dates are formatted in a named locale (`en-GB`), never the reader's.** The panel is
  English and says so, and a chart is a client component: rendered once on the server and once in
  the browser, `toLocaleDateString(undefined, …)` disagrees with itself and fails hydration.
- **A statistics range crosses as a day count, not a start date.** Only the server knows which day
  the PC is standing in, and `lib/schedule.ts` exists because the panel must not resolve a Windows
  timezone; `?days=` keeps that true. The server fills every day in the range, including the empty
  ones - a chart drawn only from the days something ran makes an occasional application look daily.
- **All three server-side processes log in one format, and every request carries a correlation id.**
  The server writes through `KidTimeConsoleFormatter`, the panel through `lib/logger.ts`, and both
  read `KIDTIME_LOG_FORMAT`, `KIDTIME_LOG_COLOR` and `KIDTIME_LOG_LEVEL` so one choice in `.env`
  changes the stack. A new log line goes through the existing logger rather than `Console.WriteLine`
  or `console.log`, and it carries no password, JWT, device token, enrollment token, or certificate
  password - see [Logging](#logging).
- Commit messages are plain imperative sentences describing the change ("Match the documented Caddy
  matcher to the deployed one"), with no conventional-commit prefixes.

## Reverse-proxy deployment

Initialize with the proxy settings so the containers publish on loopback only:

```bash
sudo ./scripts/initialize-server.sh --admin-email "parent@example.com" --bind 127.0.0.1 --web-port 3010 --base-path /kidtime --agent-url https://example.org/kidtime-api
```

Route two prefixes to the stack: the panel keeps its prefix, the agent API has its prefix stripped.

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

The proxy hop stays on HTTPS so the server container keeps using its own certificate to encrypt the
Data Protection keys; `tls_insecure_skip_verify` covers only that loopback hop. `--base-path` is
compiled into the web bundle, so changing it later means rebuilding the web image.

## Verification

Automated tests cover daily limits, manual blocks, temporary-block expiry, schedules and overnight
windows, timezone day changes, update-tolerant application identity, installer/runtime identity
reconciliation, helper-process filtering, satellite processes resolved onto their application,
crash handlers and downloaded installers kept out of the catalog, packaged components filtered from
an inferred family name, vendor agents and services retired while a launcher named like one is
kept, vendor probes, trays, proxies and versioned downloads retired while games whose names end in
a digit are kept, Microsoft-published applications recognized as such while the games among them
are not, a runtime host filtered despite reporting the family it hosts, package identities shown as
readable names, rule-change notifications, urgent running-out reminders, a delayed final
warning restated in the seconds actually left, an expired final warning dropped rather than shown,
a closing schedule window reported as pending a minute before it closes, the soonest of several
restrictions winning, a daily limit counted as a deadline only while it is being spent, the window
a grant opened over a block reported as pending when it runs out, a standing warning reporting what
is left of it so a moved deadline can be noticed,
persisted first-block grace, DNS block lists read as categories by their file name rather than by
the publisher that serves them, an unrecognized list named rather than guessed at, an overnight DNS
timetable read in its own timezone with the instant it turns over, windows that meet merged into one
stretch and a day-restricted one skipping the days it does not cover, a Firefox error page
recognized in both languages and in both generations of that page - a block page whose certificate
is for another site included - while a body heading that never reaches a window title is not, and
while a Chromium browser that titles its error page with the host is never read at all, a refused
page explained from the categories and from whichever shut set of sites comes back soonest, an
explanation that closes by telling the child to check the address or to leave a security warning
alone according to which of them they are looking at, nothing explained where filtering is off or
unread, a stale snapshot still explaining, one explanation per arrival on an error page rather than
one per sample, and silence while the PC cannot reach the server, a DNS companion answering 200
without the node's configuration signed in to again and believed on the retry, the same empty
answer twice failing the whole read rather than reading as a household that filters nothing,
schedules or domain groups that could not be read failing it too, and a household that has
genuinely switched filtering off still read as an answer,
granted extra time raising a daily limit for its own date only,
minutes alone never lifting a manual block or a schedule while the window a grant opens lifts both
from the decision until it expires, per scope and without handing over a spent daily limit, extra
time offered only once an allowance is nearly spent and still offered after it has run out, offered
while the PC or an application is blocked outright and askable there with no limit to measure,
offered on every nearly-spent application rather than only the foreground one, a second request refused while the first is unanswered, a refusal
that holds for its allowance period and lifts when the next window opens without silencing the
other scopes, the daily request cap, a request that survives a restart before it is uploaded, an
answer announced once however often the server repeats it, every stop on the request slider
accepted while anything between or beyond them is refused, automatic-update version comparison,
controlled-account SID isolation,
cached offline rules, durable pending usage, buffered usage that survives a restart, durable fault
queueing and fingerprinting, in-batch fault collapsing, spent application close leases that a
relaunch cannot inherit, a sign-out that is warned about and retried when the session outlives it,
the tray agent's own supervised launch never mistaken for the shortcut that asks it to open its
window, complete English and Russian catalogs with Russian plural agreement, language-scoped rule messages,
idle exclusion, a background application in a call counted while idle and one merely playing sound
counted only while the PC is in use, and cached app-limit evaluation.

Integration checks on a VM should use a harmless executable such as Notepad before testing game
rules:

1. allow Notepad and observe foreground usage;
2. block Notepad in the web panel and launch it again;
3. set a one-minute Notepad limit and verify it closes at exhaustion;
4. start Notepad again the instant the service closes it, and confirm the warning and the close
   repeat with the 20-second save period instead of the relaunch running on unrestricted;
5. watch that countdown to zero and confirm Notepad closes as the card reaches 0:00, not while it
   still shows seconds left, and that the card's title agrees with the number counting down;
6. let a limit run past 15, 5, and 2 minutes remaining with a game in the foreground, and confirm
   each reminder interrupts instead of fading behind it;
7. confirm the applications page reads as a list of applications: no `Microsoft.BingNews` or any
   other package identity, no WebView2, and no vendor agent, service, uninstaller, tray icon,
   hypervisor, driver probe (`vulkandriverquery`, `steamsysinfo`), `adb.exe`, or versioned
   installer download — then turn on **Show Microsoft apps** and confirm Windows' own applications
   appear, and that a Microsoft-published game such as Minecraft was visible with the switch off;
8. start Steam and confirm one card named Steam appears in the panel — no `steamwebhelper`, no crash
   handler, no `Internet Explorer` from a downloaded `KidTimeSetup.exe` — then block Steam and
   confirm the window the child is looking at is what closes;
9. join a Discord call, put a game in front, stop touching the keyboard for longer than the idle
   threshold, and confirm Discord's time keeps rising while PC time stops; then leave the call with
   a game playing music in the background and idle again, and confirm the game stops counting;
10. disconnect only the VM from the server, launch a cached-blocked app, and confirm it stays blocked;
11. reconnect and confirm pending statistics upload;
12. manually block the PC, confirm the countdown card appears with the 60-second grace period and
    no duplicate native toast beside it, expires instead of leaving a topmost window behind, and
    confirm Windows signs the session out;
13. sign in again while the rule is active and confirm the warning/sign-out cycle repeats, this
    time with 20 seconds rather than 60 - the child was not there when the block arrived;
14. set a schedule window closing a few minutes out, sit in it, and confirm the countdown card
    appears at exactly one minute before the close and the session is signed out on the boundary,
    not a minute past it; then confirm the parent's error log has no "SessionAgent has exited
    within ..." entry from that sign-out, which is what a session being torn down used to produce;
15. do the same with a daily limit a minute from running out, then leave the PC idle at under a
    minute remaining and confirm no card appears and nothing signs out until the child resumes;
16. end SessionAgent as the Standard User and confirm the service restarts it, while PC sign-out
    enforcement remains independent;
17. confirm the child never sees a Windows error dialog: any fault appears in the panel's error log
    instead, with the device, component, and stack trace, and repeats raise the count rather than
    adding rows - including a fault that stops the tray agent starting at all, which arrives both
    as the agent's own report and as the service's crash-loop error;
18. let a PC limit run down to under five minutes and confirm the Today panel offers extra time,
    that the 5-minute reminder toast and the sign-out countdown card both carry the button, and
    that pressing any of them opens the same card; confirm the slider moves only between 5 and 30
    in steps of five and that its label follows it, ask for 30 minutes, approve 20 in the panel's
    Requests page, and confirm the sign-out is cancelled, the child is told once, and the ring
    shows the new total — then deny a second request and confirm the child is told that too;
19. set a one-minute Notepad limit, let it run out, ask for extra time from the countdown card,
    and confirm the popup opens on Notepad rather than the PC, then check the Apps tab shows the
    same button on Notepad's own card and nowhere else;
20. deny that request and confirm the child cannot ask again for it while the same schedule window
    is open, that the card says when they may, and that the PC's own button still works — then
    let the next window open and confirm the button comes back on its own;
21. confirm a granted 30 minutes is gone the next day without anything being sent to remove it;
22. manually block the PC, and confirm the child can still ask: the countdown card carries the
    small button beside "Got it" while "Got it" is the accented one, the request reaches the
    Requests page, and approving 20 minutes signs nothing out and lets the child back in from the
    moment of the decision — then watch those twenty minutes run out on the wall clock and confirm
    the block returns with the ordinary warning; do the same with a blocked application and confirm
    only that application comes back;
23. switch the device language to Russian in the panel and confirm the tray tooltip and menu, the
    screen-time window, the next notification, and the countdown card all change without
    reinstalling or signing out, then block the PC and confirm the card counts down in the corner,
    never takes focus, does not overlap its own buttons with the longer Russian labels, and
    disappears on its own when the countdown ends or the parent lifts the block;
24. with no `Dns__*` configured, confirm the panel's Web filtering page says so and the child's
    window has no Internet tab at all; then point `Dns__ApiUrl`, `Dns__Username` and `Dns__Password`
    at the household's DNS companion and confirm the page reports filtering on, its button opens
    the DNS console, and the Internet tab appears on the PC within one sync;
25. check the categories on that tab against the DNS server's own block lists - a gambling or adult
    list served from `adguardteam.github.io` must not read as "Ads" - then enable a schedule that
    blocks a site group in the evening and confirm the tab names it, says whether it is open now,
    and gives the time it changes, in the child's language;
26. stop the DNS companion and confirm nothing about KidTime's own enforcement changes: rules still
    apply, synchronization still succeeds, and both the panel and the Internet tab show the last
    answer with its age rather than an error or an empty tab;
27. in Firefox on the controlled PC, open a site the DNS server blocks over **https** and confirm
    one ordinary toast appears naming what the home network blocks - and, if a site group is shut,
    when it comes back - in the child's language. Check what the browser itself drew: a DNS server
    answering with an address of its own produces a security warning ("Warning: Security Risk"),
    not "Server Not Found", and the toast must end by telling the child to leave that warning alone
    rather than to check the address. Reload the page several times and confirm no second toast,
    then confirm a site that is not blocked produces none at all. Unplug the network and open
    anything: the same error page must produce **no** toast, because a home network that is down is
    not the filter. Then confirm the whole thing stays quiet where no `Dns__*` is configured;
28. search the Start menu on the controlled PC for "KidTime" and confirm it is found with its own
    icon, that opening it brings up the screen-time window, and that the same shortcut is on the
    desktop. Check Task Manager while doing it: there must still be exactly one
    `KidTime.SessionAgent` process, because the shortcut signals the running agent instead of
    starting a second one. Close the window and open it again from the shortcut; then, as the
    child, try to delete the desktop icon and confirm Windows refuses. Finally remove KidTime from
    the screen-time window and confirm both the Start menu entry and the desktop icon go with it;
29. turn on **Open it when the child signs in** on the device page, sign the child out and back
    in, and confirm the screen-time window comes up by itself once. Close it, end
    `KidTime.SessionAgent` from Task Manager, and confirm the service brings the agent back
    without the window reopening — one window per sign-in, not one per agent. Then restart
    `KidTimeControl` and confirm the same; only signing in again brings it back;
30. read the server's log with `docker compose logs -f server`: one line per request with the
    method, path, status, duration and either the parent's e-mail or the device's short id, the
    same `req=` id on the panel's line for the same click, and that id on the response's
    `X-Request-Id` header in the browser's network tab. Sign in with the wrong password and confirm
    one warning line on each side; stop the `server` container and confirm the panel's log names
    the actual cause (`ECONNREFUSED`) rather than only "unavailable".

On a disposable PC, verify enrollment end to end (Connect stays disabled until server URL,
enrollment code, and child account are all valid; an expired code is rejected; the panel switches to
connected on its own) and both removal paths (the panel record disappears; then Remove KidTime in
the screen-time window rejects invalid parent credentials, and with valid ones removes
`KidTimeControl`, `C:\Program Files\KidTime`, and `C:\ProgramData\KidTime`).

## Troubleshooting

- **Setup cannot reach the API:** confirm the controlled PC can reach TCP 5081 on the server and that
  the URL shown by Add device resolves there; check `sudo ufw status`. The one-time code carries the
  certificate pin automatically.
- **Setup says no standard account was found:** create or enable a Standard User, then select
  Refresh. Administrator and disabled accounts are excluded on purpose.
- **Add device says the setup file is unavailable:** run `build-agent.ps1`, upload
  `artifacts/releases`, publish with `publish-agent-release.sh`. If the files are already in
  `/opt/kidtime/releases`, confirm they are world-readable — the server container runs as non-root.
- **Setup reports the PC is already connected:** remove KidTime from that PC first; a second
  enrollment of the same PC is refused deliberately.
- **Service starts but no UI agent appears:** confirm the signed-in profile is the selected Standard
  User, and inspect service logs for `WTSQueryUserToken`/`CreateProcessAsUser` failures.
- **A newly created child profile is not selectable:** wait up to a minute for the service to report
  local accounts, refresh the device page, and confirm the account is enabled and not an
  administrator.
- **Rules show pending:** check `LastSeenUtc`, the service's HTTPS connectivity, and that the server
  URL uses the host's LAN address rather than `localhost`.
- **An app is not listed:** start it once. Only parent-manageable user applications are registered.
- **A card disappeared from the applications page:** the catalog filter is applied on read, so
  widening it retires entries already in the database — a helper, crash handler, or downloaded
  installer that used to have a card stops having one as soon as the server runs the new code. A
  satellite that belongs to a real application (`steamwebhelper` to Steam) is not lost but merged:
  `ApplicationCatalogReconciler` folds its rules and usage into the principal at server start, so
  restart the server container once after deploying rather than re-creating the rule.
- **Usage is lower than elapsed login time:** expected — only non-idle foreground time counts.
- **The sign-out countdown did not start a minute early:** only a deadline the rules can name in
  advance can be counted down to. A schedule close and a grant's window running out are named; a
  daily limit is named only while the child is actually spending it, so an idle PC gets the warning
  when the block arrives instead. A manual block has no deadline at all and is warned about after
  the fact, with the full minute.
- **The window does not open when the child signs in:** it is a rule like any other, so check
  first that the revision on the device page has caught up - the PC has to have synchronized
  since the switch was turned on. After that, the opening is spent once per sign-in and the
  answer is persisted, so signing out and in again is the only way to see it; restarting
  `KidTimeControl`, or an automatic update doing so, deliberately does not bring it back. If it
  still does not appear, no tray agent is running at all - see the tray-agent entries below,
  because that is the same fault, and a child in that state has no window to open.
- **The KidTime shortcut is missing from the Start menu or the desktop:** the service writes both
  when it starts, so restart `KidTimeControl` and they come back; they are not written by setup, and
  re-running setup on an enrolled PC is refused anyway. If they still do not appear, the service log
  says which folder Windows would not give it or would not let it write. A shortcut that opens
  nothing means no agent answered - see the entry below, because that is the same fault.
- **Clicking the shortcut does nothing:** it does not start the agent, it asks the running one to
  open its window, and it waits about five seconds for an answer in case the service is mid-restart.
  Nothing happening therefore means no tray agent is running at all, which is the fault below and is
  visible in the parent's error log. `%LOCALAPPDATA%\KidTime\logs` on the PC records the click that
  went unanswered.
- **The child has no tray icon, window, or notifications while rules still apply:** the tray agent
  is failing to start and the service is relaunching it every two seconds. The error log carries
  both the agent's own fault and a "SessionAgent has exited within ... times in a row" error from
  the service; `%LOCALAPPDATA%\KidTime\logs\session-agent-faults.ndjson` on the PC has the stack
  trace either way. Enforcement is unaffected, which is why this can go unnoticed.
- **A "SessionAgent has exited within 1s of starting 5 times in a row" error with nothing else
  beside it:** on agents before this was fixed, that was usually a forced sign-out rather than a
  fault. The session survives `WTSLogoffSession` by several seconds while it is torn down, and the
  agent relaunched into it died every two seconds until it went away. The current service does not
  count those, so a report that still appears is a real one - and it now carries the agent's exit
  code, with `0xE0434352` meaning a .NET exception escaped, which the agent's own fault report on
  the same PC will name. `0xC000026B` beside a "still signed in after it was signed out" error is a
  sign-out that took longer than thirty seconds rather than one that failed; the current service
  recognizes it and reports neither.
- **The error log stays empty after a crash:** reports ride the next synchronization, so a PC that
  is offline delivers them when it reconnects. Check `LastSeenUtc`, then
  `%LOCALAPPDATA%\KidTime\logs\session-agent-faults.ndjson` (queued in the child's session) and
  `C:\ProgramData\KidTime\logs\diagnostics.ndjson` (queued in the service).
- **The same error keeps coming back after being marked handled:** marking handled is not a fix.
  The next occurrence reopens the row and raises its count, which is the intended signal that the
  fault is still happening.
- **The child says the "ask for more time" button is not there:** it appears when a daily limit has
  five minutes or less left, and whenever the PC or the application is actually shut — a spent
  limit, a manual block, or a closed schedule window. A request already waiting on an answer hides
  it until the parent decides, and so does a refusal, until the next schedule window opens (or the
  next day, with no schedule configured). The card says which of those it is. On the countdown card
  the button is the small quiet one beside "Got it", which is deliberate.
- **An approved grant has not reached the PC:** a decision bumps the rule revision like any other
  change, so it lands with the next sync. Check `LastSeenUtc` and that the revision on the device
  page has caught up. A grant adds minutes to the device-local date the child asked on and adds
  nothing to the limit once that date has passed.
- **A grant given during a block ran short:** that half of a grant is wall-clock time from the
  moment the parent approved it, not from the moment the PC heard about it, so a PC that was
  offline or switched off spends the window while nobody is using it. The minutes added to a daily
  limit are not affected — those are still spent in active foreground time.
- **The toast's extra-time button does nothing:** the button reaches the running agent through the
  notification COM server, which an unusual machine can refuse to register. The screen-time window
  and the countdown card are the paths that do not depend on it; check
  `%LOCALAPPDATA%\KidTime\logs` for the subscription failure.
- **The PC is still speaking English after switching the language:** the language is part of the
  rules, so it lands with the next sync. Check `LastSeenUtc` and that the rule revision on the
  device page has caught up.
- **The Internet tab is missing on the child's PC:** it is drawn only where the server has a DNS
  server configured, so check the panel's Web filtering page first. If that page says filtering is
  on, the PC simply has not synchronized yet - the snapshot rides the ordinary sync, so check
  `LastSeenUtc`.
- **The Web filtering page says the DNS server did not answer:** filtering itself is unaffected, it
  runs on the DNS server and not here, and a page that had read it before goes on showing that
  reading with its age. The usual cause is the certificate: the companion self-signs,
  so `Dns__PinnedCertificateSha256` has to carry its SHA-256, which
  `openssl s_client -connect HOST:8095 </dev/null | openssl x509 -noout -fingerprint -sha256` prints.
  After that, check that `Dns__ApiUrl` is reachable **from the server container** rather than from
  the machine or the browser, which is the distinction that matters: a companion published behind
  an allowlisted proxy port answers an admin's browser and drops the container's packets, and the
  failure that leaves in the log is `HttpClient.Timeout ... elapsing` rather than a refusal, because
  a dropped SYN never becomes an error. `docker exec <server> bash -c '</dev/tcp/HOST/PORT'` settles
  it in a second; the answer is usually to reach the companion over its own Docker network - see
  [Web filtering](#web-filtering-technitium-dns) - rather than to open the port. Then check that
  the credentials are the ones the DNS console takes.
- **The Web filtering page says filtering is off, and the DNS console says it is on:** on servers
  before this was fixed that was the commonest thing this integration did wrong, and it had no
  symptom at all - the page read "Off" with a timestamp from a minute ago and the server log was
  clean. The cause is in the companion rather than in KidTime: `docker logs technitium-companion`
  shows `Technitium rejected token for node "node1" ... re-login required` once, and then
  `Failed to load Advanced Blocking config from node "node1": Authentication required` on every
  read after it. The current server treats that answer as a failed read, logs it, keeps the last
  good one and signs in again, which mints a new node token and recovers within a refresh. On an
  older server the fix is to restart the `server` container, which logs in afresh.
- **The child was not told why a blocked site would not open:** the explanation needs four things
  at once, and the first one it fails is the answer. It is drawn only in a Gecko browser, because
  Firefox titles its error page with a sentence and Chromium titles it with the host - a child
  using Chrome gets nothing, by design. The browser's interface language has to be one the
  detector knows (English or Russian), which is not necessarily the language the parent chose for
  the PC. The service has to be reaching the server, so a PC that is offline stays quiet on
  purpose. And `Dns__*` has to be configured and blocking something - check the panel's Web
  filtering page. After a successful explanation there is ten minutes of quiet before another.

  If all four hold and nothing appears, read the browser's **tab title**, which is the only thing
  the detector sees. Firefox changes it between versions and between the two generations of its
  error page, and a title not in `BrowserPageErrorDetector.ErrorTitles` is silence by design. The
  authoritative strings are in Firefox's own `omni.ja` and its langpacks - on the machine itself,
  `localization/<locale>/toolkit/neterror/{netError,certError}.ftl` - and the ids to look for are
  `neterror-page-title`, `neterror-dns-not-found-title`, `fp-certerror-page-title` and
  `certerror-page-title`. Do not add a body heading such as "Secure Connection Failed": Firefox
  draws those inside the page and they never reach a window title.
- **The child was told about filtering for a site nothing blocks:** a page that fails because the
  site is genuinely down, or because the PC's clock is wrong, looks from outside the browser
  exactly like one the filter refused. Nothing distinguishes them without reading the address,
  which KidTime does not do. The message is written to survive this - it states what the household
  blocks rather than claiming that page was blocked, and it closes either by asking the child to
  check the address or, where they are looking at a security warning, by telling them not to click
  past it.
- **A category on the Internet tab reads wrong:** the subject is worked out from the block list's
  address, and an address that says nothing lands in "Other" on purpose. A list that is genuinely
  mislabelled belongs in `DnsFilterPolicy` - `KnownListNames` for a file name like `tif.txt`,
  `AdGuardRegistryLists` for one of AdGuard's numbered lists.
- **A site group the parent defined is not on the Internet tab:** only sets that actually restrict
  something are listed. A domain group with no enabled blocking schedule and no binding to the
  filtering group restricts nothing, and showing it would be a rule the child would act on that does
  not exist.
- **The server log is a wall of framework noise, or has no colour:** both are settings.
  `KIDTIME_LOG_FORMAT=json` swaps the whole format for structured events; `KIDTIME_LOG_LEVEL=Debug`
  adds the health probes and hub traffic that `Information` leaves out. Colour is `auto` by
  default, which sees a pipe inside a container and gives up - compose passes `always`, so a
  deployment that lost its colour has overridden `KIDTIME_LOG_COLOR`, or is being read through
  something that sets `NO_COLOR`.
- **A `Failed executing DbCommand` error on the very first start:** that is Entity Framework asking
  an empty database for its migration history, which cannot answer yet. The `Applying migration`
  lines immediately after it are the real state. It happens once per fresh database.
- **A request appears in the panel's log but not the server's:** the panel answered it by itself -
  no session cookie, or a page that needed no API call. Otherwise search the server's log for the
  same `req=` id; the panel logs one whenever it reaches the API, and both containers key on it.
- **Changing the `.env` admin password has no effect:** those variables seed only the first parent
  account. Do not delete PostgreSQL data merely to rotate a password.
