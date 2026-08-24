# Architecture and security decisions

## Enforcement boundary

`ControlService` is the only component trusted to hold device credentials, cache rules, count accepted samples, decide allow/block state, terminate blocked processes, and supervise `SessionAgent`. It runs as LocalSystem. `SessionAgent` runs unelevated in the logged-in desktop because services cannot safely provide ordinary interactive UI. The parent selects one enabled Standard User in the web panel. Its Windows SID is cached in the rule snapshot; the service launches SessionAgent, counts usage, terminates apps, and signs out sessions only when that exact SID owns the active console session. With no selected SID, enforcement is inactive. Administrator accounts are reported but cannot be selected.

The local named pipe grants transport-level read/write to authenticated local users so the Standard User’s SessionAgent can connect, but each connection is accepted only when the kernel-reported client process ID matches the exact SessionAgent launched and supervised by the service. The SessionAgent process DACL grants control only to LocalSystem and administrators. Messages are length-bounded and contain telemetry only; the client cannot submit rules, commands, or privileged actions through IPC.

## Rule precedence

A PC or application is unavailable when any relevant rule denies it:

1. active manual block;
2. active-time daily total greater than or equal to the limit;
3. current device-local time outside the weekly schedule.

PC rules are evaluated before the interactive desktop is exposed. Application rules are evaluated at process discovery and again while the application is foreground, so reaching a limit closes an already-running application too. Overnight schedule windows are evaluated against both the current and previous weekday.

## Active-time and clock strategy

`SessionAgent` samples the foreground window/process and `GetLastInputInfo` every two seconds. `ControlService` applies the configured idle threshold and counts only accepted foreground samples.

Durations come from `Stopwatch` monotonic elapsed milliseconds, not subtraction of wall-clock timestamps. A single sample delta is capped at 30 seconds to prevent suspend/resume or a stalled client from creating a large jump. Fractional seconds remain in memory until a full second can be persisted.

The service anchors its working UTC clock to monotonic time. Every successful server sync replaces that anchor with `ServerUtcNow`, preventing an ordinary wall-clock edit during the running service from instantly changing schedules or resetting a daily limit. Device-local dates and weekdays are computed from the configured Windows timezone. A LocalSystem service restart while completely offline necessarily begins from the machine clock; defending that boundary robustly would require a trusted external clock or stronger anti-tamper mechanism and is documented rather than hidden.

## Offline storage and synchronization

SQLite uses WAL, `synchronous=FULL`, and transactions. The database stores:

- the last validated rule snapshot and revision;
- per-local-date PC and application totals;
- pending usage totals;
- discovered application descriptors and sync state;
- durable idempotent usage batches.

Pending counters are moved into a batch and zeroed in the same transaction. The server records every batch ID before acknowledging it, so an uncertain retry cannot double-count. A batch is deleted locally only after a successful response.

## Application identity

The stable identity key prioritizes:

1. MSIX package family identity;
2. signature publisher plus product and executable name;
3. signature publisher plus original filename when product metadata is absent;
4. product plus original filename;
5. normalized executable path and filename as a last resort.

File version and SHA-256 may be recorded for diagnosis but do not participate in a signed application’s stable key. This lets normal updates move paths, change hashes, and change versions without losing the rule. Matching also uses a weighted publisher/product/original-name score for diagnostics and migration.

The management catalog includes interactive Win32 and MSIX applications such as browsers, Notepad, Xbox, Calculator, and Photos. When Windows places a packaged app inside `ApplicationFrameHost`, SessionAgent resolves the packaged child process before creating its identity. It excludes Windows operating-system processes, services, helper packages, compatibility components, runtimes, updaters, installers, and KidTime itself. Foreground PC time still counts when a non-manageable shell surface is active, but those components do not receive application cards or rules.

## Authentication and secrets

- Parent passwords use ASP.NET Core’s versioned `PasswordHasher` format.
- Parent API tokens are signed with a random, installation-specific HMAC key and expire after 12 hours.
- The browser never exposes the JWT to client JavaScript; Next.js stores it in an HTTP-only, strict same-site cookie and proxies API calls.
- Enrollment tokens are random, single-use, short-lived, and stored server-side only as SHA-256 digests.
- Device credentials are independent random values stored server-side only as digests.
- On Windows, the raw device credential is DPAPI LocalMachine protected and the file ACL permits only LocalSystem and administrators.
- ASP.NET Data Protection keys persist in a dedicated Docker volume, are writable only by the non-root application UID, and are encrypted with the mounted HTTPS certificate.
- Logs intentionally omit passwords, JWTs, device tokens, enrollment tokens, and certificate passwords.

The development server certificate is explicitly pinned by SHA-256 on the Windows agent. Production should use a certificate valid and trusted for the chosen parent-PC hostname.

## PC blocking and anti-tamper boundary

When a PC rule blocks access, the LocalSystem service queues a tagged native Windows final-warning notification that states the reason, usage where applicable, and the next available time. The first warning in that PC restriction episode lasts 60 seconds; signing in again while the same restriction remains active gets 20 seconds. This grace state is persisted locally across service restarts. The service owns the monotonic deadline and then calls `WTSLogoffSession`; notification delivery is never trusted for enforcement. The notification has an explicit expiration and no custom topmost window exists to become stranded. If the parent dismisses the PC restriction during the countdown, a keyed dismissal hides the active toast, removes it from Notification Center, and cancels sign-out. When the rule ends, a normal native Windows notification tells the interactive user that the PC is available and repeats the prior reason.

KidTime does not draw custom notification, popup, or blocker windows. SessionAgent emits native Windows toasts marked with the supported urgent scenario, high priority, and explicit reminder audio. Its read-only tray dashboard composes maintained WPF UI FluentWindow, Card, ProgressRing, InfoBar, Badge, SymbolIcon, menu, and tray controls, follows the Windows system theme/accent, and receives status only through the process-validated pipe. Important notifications are the Windows-supported mechanism for breaking through Focus Assist/Do Not Disturb without changing or corrupting the user's global setting; Windows can ask the user once whether KidTime may send important notifications. The LocalSystem service restarts SessionAgent every two seconds if it exits. The install directory is read/execute-only for ordinary users, service data is accessible only to LocalSystem and administrators, the service control ACL denies stop/configure rights to interactive users, service recovery is enabled, and the SessionAgent process DACL prevents a Standard User from terminating or injecting into it.

PC time-limit and schedule revisions queue a normal Windows notification for the controlled session regardless of the foreground app. Application time-limit and schedule revisions queue a normal notification only if that application is currently open. The 15-, 5-, and 2-minute reminders also remain normal Windows notifications. The final forced-close warning is a tagged, long-duration native notification with a deadline-based expiration. The first blocked launch in a restriction episode gets 60 seconds to save work; later launches during that same episode get 20 seconds. This first-launch grace is persisted locally across service restarts. If the application exits before its countdown ends, the service sends a keyed dismissal so the notification is hidden and removed immediately.

Agent releases are served only to enrolled device credentials over the same certificate-pinned HTTPS channel used for rules and usage. Each manifest contains the release version, exact byte size, and SHA-256 digest; both the server and agent verify the package before installation. The LocalSystem updater stages outside the install directory, retains a rollback copy, replaces only service binaries, and restarts the protected service. Device enrollment credentials, cached rules, usage, and logs remain under ProgramData. The web API compares the heartbeat-reported assembly version with the published manifest and exposes current, outdated, downloading, installing, or failed state to the parent UI.

A local administrator remains outside the security boundary: an administrator can take ownership, alter files, stop protected services, boot to recovery, or change accounts. KidTime therefore requires a separate Standard User for the controlled child. The administrator account used for SSH deployment must not be the child’s everyday account.

## Explicit privacy boundary

No component contains web filtering, browser hooks, DNS proxying, URL capture, HTTPS interception, packet inspection, keylogging, screen capture, camera/microphone access, message collection, or location tracking. Foreground window titles are transported locally for diagnostics but are not persisted or uploaded by the current server contract.
