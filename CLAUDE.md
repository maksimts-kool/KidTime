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

```bash
dotnet test tests/KidTime.Domain.Tests
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
so usage is always lower than elapsed login time.

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
native Windows notification when the card cannot be drawn. The first
warning in a PC restriction episode lasts 60 seconds; signing in again while the same restriction is
active gets 20 seconds. That grace state is persisted locally across service restarts. The service
owns the monotonic deadline and then calls `WTSLogoffSession`; **notification delivery is never
trusted for enforcement.** The warning carries an explicit expiration, and the card cannot strand
itself either - the constraints below are what guarantee that.

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
rather than a `TabControl`, because only the visible panel then stays in the visual tree. About
carries the installed version, the privacy summary in the child's own words, and the removal flow.

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

**Prefer removing recurring work over making it faster**, and keep enforcement timing decisions -
sign-out deadlines, close deadlines - on the monotonic clock in the service, where interval changes
cannot move them.

### Privacy boundary

No component contains web filtering, browser hooks, DNS proxying, URL capture, HTTPS interception,
packet inspection, keylogging, screen capture, camera/microphone access, message collection, or
location tracking. Foreground window titles travel locally for diagnostics but are not persisted or
uploaded by the current server contract. **Features that would cross this line are out of scope by
design** — do not add them, and do not extend the contract to upload window titles.

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
  well enough; the instants cross as UTC and `lib/schedule.ts` renders them in the child's clock
  time, since 21:30 has to mean 21:30 on the controlled PC whichever timezone the parent is
  reading from.
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
persisted first-block grace, granted extra time raising a daily limit for its own date only,
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
complete English and Russian catalogs with Russian plural agreement, language-scoped rule messages,
idle exclusion, and cached app-limit evaluation.

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
9. disconnect only the VM from the server, launch a cached-blocked app, and confirm it stays blocked;
10. reconnect and confirm pending statistics upload;
11. manually block the PC, confirm the countdown card appears with the 60-second grace period and
    no duplicate native toast beside it, expires instead of leaving a topmost window behind, and
    confirm Windows signs the session out;
12. sign in again while the rule is active and confirm the warning/sign-out cycle repeats;
13. end SessionAgent as the Standard User and confirm the service restarts it, while PC sign-out
    enforcement remains independent;
14. confirm the child never sees a Windows error dialog: any fault appears in the panel's error log
    instead, with the device, component, and stack trace, and repeats raise the count rather than
    adding rows - including a fault that stops the tray agent starting at all, which arrives both
    as the agent's own report and as the service's crash-loop error;
15. let a PC limit run down to under five minutes and confirm the Today panel offers extra time,
    that the 5-minute reminder toast and the sign-out countdown card both carry the button, and
    that pressing any of them opens the same card; confirm the slider moves only between 5 and 30
    in steps of five and that its label follows it, ask for 30 minutes, approve 20 in the panel's
    Requests page, and confirm the sign-out is cancelled, the child is told once, and the ring
    shows the new total — then deny a second request and confirm the child is told that too;
16. set a one-minute Notepad limit, let it run out, ask for extra time from the countdown card,
    and confirm the popup opens on Notepad rather than the PC, then check the Apps tab shows the
    same button on Notepad's own card and nowhere else;
17. deny that request and confirm the child cannot ask again for it while the same schedule window
    is open, that the card says when they may, and that the PC's own button still works — then
    let the next window open and confirm the button comes back on its own;
18. confirm a granted 30 minutes is gone the next day without anything being sent to remove it;
19. manually block the PC, and confirm the child can still ask: the countdown card carries the
    small button beside "Got it" while "Got it" is the accented one, the request reaches the
    Requests page, and approving 20 minutes signs nothing out and lets the child back in from the
    moment of the decision — then watch those twenty minutes run out on the wall clock and confirm
    the block returns with the ordinary warning; do the same with a blocked application and confirm
    only that application comes back;
20. switch the device language to Russian in the panel and confirm the tray tooltip and menu, the
    screen-time window, the next notification, and the countdown card all change without
    reinstalling or signing out, then block the PC and confirm the card counts down in the corner,
    never takes focus, does not overlap its own buttons with the longer Russian labels, and
    disappears on its own when the countdown ends or the parent lifts the block.

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
- **The child has no tray icon, window, or notifications while rules still apply:** the tray agent
  is failing to start and the service is relaunching it every two seconds. The error log carries
  both the agent's own fault and a "SessionAgent has exited within ... times in a row" error from
  the service; `%LOCALAPPDATA%\KidTime\logs\session-agent-faults.ndjson` on the PC has the stack
  trace either way. Enforcement is unaffected, which is why this can go unnoticed.
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
- **Changing the `.env` admin password has no effect:** those variables seed only the first parent
  account. Do not delete PostgreSQL data merely to rotate a password.
