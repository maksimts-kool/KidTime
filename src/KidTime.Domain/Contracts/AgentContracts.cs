using KidTime.Domain.Applications;
using KidTime.Domain.Localization;
using KidTime.Domain.Rules;

namespace KidTime.Domain.Contracts;

public sealed record DeviceEnrollmentRequest(
    string EnrollmentToken,
    string DeviceName,
    string WindowsVersion,
    string TimeZoneId,
    WindowsUserAccount? ControlledWindowsUser = null);

public sealed record DeviceEnrollmentResponse(
    Guid DeviceId,
    string DeviceToken,
    DeviceRuleSnapshot Rules);

public sealed record DeviceHeartbeatRequest(
    string? LoggedInUser,
    string? ForegroundApplication,
    string? ForegroundIdentityKey,
    int TodayActiveSeconds,
    DateTimeOffset ClientUtcNow,
    long RuleRevision,
    IReadOnlyList<WindowsUserAccount>? WindowsUsers = null,
    string? AgentVersion = null,
    string? AgentUpdateStatus = null,
    string? AgentUpdateError = null,
    DateTimeOffset? AgentUpdateCheckedAtUtc = null);

public sealed record AgentUpdateManifest(
    string Version,
    string Sha256,
    long SizeBytes);

public sealed record WindowsUserAccount(
    string Sid,
    string AccountName,
    string DisplayName,
    bool IsEnabled,
    bool IsAdministrator);

public sealed record UsageDelta(
    DateOnly LocalDate,
    string? ApplicationIdentityKey,
    int ActiveSeconds);

public sealed record UsageBatchRequest(
    Guid BatchId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<UsageDelta> Deltas);

public sealed record DiscoveredApplicationRequest(
    string IdentityKey,
    ApplicationDescriptor Descriptor,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record AgentSyncResponse(
    DeviceRuleSnapshot Rules,
    IReadOnlyList<AgentCommand> Commands,
    DateTimeOffset ServerUtcNow,
    IReadOnlyList<TimeExtensionDecision>? TimeExtensions = null);

/// <summary>
/// One child's request for more time, on its way to the parent. The agent mints the id, so an
/// upload retried after an uncertain response lands on the same row instead of asking twice.
/// <paramref name="ApplicationIdentityKey"/> is null when the request is for the PC's own screen
/// time rather than for one application that is running out.
/// </summary>
public sealed record TimeExtensionRequest(
    Guid RequestId,
    DateTimeOffset RequestedAtUtc,
    DateOnly LocalDate,
    int RequestedMinutes,
    string? ApplicationIdentityKey,
    string DisplayName);

public sealed record TimeExtensionBatch(IReadOnlyList<TimeExtensionRequest> Requests);

public enum TimeExtensionStatus
{
    Pending,
    Approved,
    Denied
}

/// <summary>
/// What the parent decided, on its way back. The granted minutes are already in the rule
/// snapshot by the time this arrives; this exists so the child can be told, once, what happened.
/// </summary>
public sealed record TimeExtensionDecision(
    Guid RequestId,
    TimeExtensionStatus Status,
    int GrantedMinutes,
    string DisplayName,
    DateTimeOffset? DecidedAtUtc);

public sealed record AgentCommand(Guid Id, string Type, DateTimeOffset CreatedAtUtc);

public sealed record SessionUsageSample(
    long Sequence,
    long MonotonicElapsedMilliseconds,
    bool IsIdle,
    int IdleSeconds,
    int ProcessId,
    string? WindowTitle,
    ApplicationDescriptor? ForegroundApplication,
    bool StatusRequested = true,
    IReadOnlyList<AudibleApplication>? AudibleApplications = null);

/// <summary>
/// An application holding an active Windows audio session, whether or not it is in front. A
/// child in a Discord call while playing a game is using Discord the whole time, and the
/// foreground window alone would never see it. <paramref name="IsCapturing"/> means the
/// application holds the microphone open - which is what a call is, so it counts even while
/// nobody touches the keyboard. Only the fact of the session crosses the pipe: no audio is ever
/// read, which is the same thing Windows' own microphone-in-use indicator shows.
/// </summary>
public sealed record AudibleApplication(ApplicationDescriptor Application, bool IsCapturing);

/// <summary>
/// One agent-side fault worth showing the parent. Reports are diagnostics only: they never
/// carry credentials, window titles, or any other content outside the documented privacy scope.
/// </summary>
public sealed record DiagnosticReport(
    Guid ReportId,
    DateTimeOffset OccurredAtUtc,
    string Component,
    string Severity,
    string Message,
    string? ExceptionType = null,
    string? Detail = null,
    string? AgentVersion = null);

public sealed record DiagnosticReportBatch(IReadOnlyList<DiagnosticReport> Reports);

public static class DiagnosticComponents
{
    public const string ControlService = "ControlService";
    public const string SessionAgent = "SessionAgent";
}

/// <summary>
/// What a SessionAgent exit code tells the service that supervises it.
/// </summary>
public static class SessionAgentExitCodes
{
    /// <summary>
    /// Windows ended the session - the child signed out, restarted, or shut down. The session
    /// keeps existing for several seconds while it is torn down and kills anything started in it,
    /// so the service must not relaunch into it and read those deaths as a crash loop.
    /// </summary>
    public const int SessionEnded = 0x4B540001;
}

public static class DiagnosticSeverities
{
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Fatal = "Fatal";
}

public sealed record ParentRemovalRequest(string Email, string Password);

/// <summary>The outcome of a removal request; <paramref name="Message"/> is already localized.</summary>
public sealed record DeviceRemovalResult(bool Accepted, string Message);

/// <summary>
/// The child asking for more time, as it crosses the named pipe. It carries an amount and a
/// scope and nothing else: the unelevated agent cannot grant time, only ask for it, and the
/// LocalSystem service checks the amount, the scope, and how little is actually left before it
/// records anything. Widening this into something that could change a rule would hand the
/// controlled account the thing the whole boundary exists to keep from it.
/// </summary>
public sealed record TimeExtensionSubmission(int Minutes, string? ApplicationIdentityKey);

/// <summary>The answer to one submission; <paramref name="Message"/> is already localized.</summary>
public sealed record TimeExtensionSubmissionResult(bool Accepted, string Message);

public sealed record SessionAgentRequest(
    SessionUsageSample? UsageSample = null,
    ParentRemovalRequest? RemovalRequest = null,
    IReadOnlyList<DiagnosticReport>? Diagnostics = null,
    TimeExtensionSubmission? TimeExtension = null);

public sealed record SessionAgentResponse(
    EnforcementState? Enforcement = null,
    DeviceRemovalResult? Removal = null,
    TimeExtensionSubmissionResult? TimeExtension = null);

/// <summary>
/// A message for the controlled user. <paramref name="IsUrgent"/> selects the Windows "urgent"
/// toast scenario, which stays on screen and breaks through Focus Assist. It carries the final
/// warning before Windows signs the session out or closes an application, and the running-out
/// reminders - a child deep in a game does not see an ordinary toast fade, and a limit warning
/// nobody read is the same as no warning. Rule changes, availability, and completed updates stay
/// ordinary toasts.
///
/// <paramref name="CountdownSeconds"/> is whatever is left when the message is handed to the
/// agent, not when the service decided to send it, so the seconds on the child's card are the
/// seconds the service will actually act on.
/// </summary>
public sealed record UserNotification(
    string Title,
    string Message,
    int? CountdownSeconds = null,
    string? PersistentNotificationKey = null,
    bool DismissPersistentNotification = false,
    bool IsUrgent = false);

/// <summary>Why the agent is or is not talking to the server, as a code the UI renders itself.</summary>
public enum ServerConnectionState
{
    Connecting,
    NotEnrolled,
    Connected,
    Offline
}

/// <summary>
/// Connection state for the child's window. <paramref name="State"/> is a code rather than a
/// sentence so the unelevated agent can phrase it in the configured language;
/// <paramref name="LastSynchronizationError"/> stays raw because it is a technical detail.
/// </summary>
public sealed record ServerConnectionStatus(
    bool IsConnected,
    ServerConnectionState State,
    DateTimeOffset? LastSuccessfulContactUtc,
    DateTimeOffset? LastSuccessfulSynchronizationUtc,
    string? LastSynchronizationError);

/// <summary>
/// Whether the child may ask for more time right now, and how the last ask went. One offer is
/// produced for the PC and one for the application in the foreground, and only while that
/// allowance is nearly spent - which is exactly when the buttons are worth drawing.
/// </summary>
public enum TimeExtensionOfferState
{
    /// <summary>Nothing is running out; the child is not offered anything.</summary>
    Unavailable,

    /// <summary>Running out, nothing asked yet.</summary>
    Available,

    /// <summary>Asked, waiting on the parent.</summary>
    Pending,

    /// <summary>The parent granted it; the extra time is already in the limit.</summary>
    Granted,

    /// <summary>The parent said no. The child may ask again, up to the daily cap.</summary>
    Denied
}

public sealed record TimeExtensionOffer(
    string? ApplicationIdentityKey,
    string DisplayName,
    TimeExtensionOfferState State,
    int Minutes,
    int RemainingSeconds);

public sealed record TimeAllowanceStatus(
    bool IsAllowed,
    BlockReason Reason,
    string Message,
    int TodayActiveSeconds,
    int? DailyLimitSeconds,
    int? DailyRemainingSeconds,
    bool HasWeeklySchedule,
    bool IsWithinSchedule,
    DateTimeOffset? ScheduleAvailableSinceUtc,
    DateTimeOffset? ScheduleAvailableUntilUtc,
    DateTimeOffset? ScheduleAvailableAgainUtc,
    /// <summary>
    /// Extra time granted today, already counted inside <see cref="DailyLimitSeconds"/>. It is
    /// reported separately only so the child's window can say where the difference came from.
    /// </summary>
    int BonusSeconds = 0);

public sealed record ApplicationTimeStatus(
    string IdentityKey,
    string DisplayName,
    bool IsManuallyBlocked,
    TimeAllowanceStatus Allowance,
    /// <summary>
    /// Whether this application can be asked about right now. Present only while its own
    /// allowance is nearly spent, so the Apps tab draws a button beside the one running out
    /// rather than beside every application the parent has ever limited.
    /// </summary>
    TimeExtensionOffer? Extension = null);

public sealed record SessionStatusSnapshot(
    DateTimeOffset GeneratedAtUtc,
    string? ControlledUserName,
    long RuleRevision,
    ServerConnectionStatus Server,
    TimeAllowanceStatus ScreenTime,
    IReadOnlyList<ApplicationTimeStatus> Applications);

/// <summary>
/// The answer to one foreground sample. <paramref name="Language"/> rides on every exchange, not
/// only on the ones carrying a full status snapshot, so the tray agent knows which language to
/// paint its own chrome in from the very first reply.
///
/// <paramref name="Notifications"/> is everything the service has waiting, not one message per
/// exchange. Handing them out one at a time put every message behind a two-second sample, which
/// a final warning cannot afford: its countdown was drawn from the moment it arrived while the
/// service was already counting down from the moment it was queued.
/// </summary>
public sealed record EnforcementState(
    bool IsPcBlocked,
    RuleDecision PcDecision,
    RuleDecision? ForegroundApplicationDecision,
    int TodayActiveSeconds,
    int? DailyLimitSeconds,
    int RemainingSeconds,
    IReadOnlyList<UserNotification> Notifications,
    SessionStatusSnapshot? Status = null,
    AgentLanguage Language = AgentLanguage.English,
    IReadOnlyList<TimeExtensionOffer>? ExtensionOffers = null);
