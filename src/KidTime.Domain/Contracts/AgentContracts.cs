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
    DateTimeOffset ServerUtcNow);

public sealed record AgentCommand(Guid Id, string Type, DateTimeOffset CreatedAtUtc);

public sealed record SessionUsageSample(
    long Sequence,
    long MonotonicElapsedMilliseconds,
    bool IsIdle,
    int IdleSeconds,
    int ProcessId,
    string? WindowTitle,
    ApplicationDescriptor? ForegroundApplication,
    bool StatusRequested = true);

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

public static class DiagnosticSeverities
{
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Fatal = "Fatal";
}

public sealed record ParentRemovalRequest(string Email, string Password);

/// <summary>The outcome of a removal request; <paramref name="Message"/> is already localized.</summary>
public sealed record DeviceRemovalResult(bool Accepted, string Message);

public sealed record SessionAgentRequest(
    SessionUsageSample? UsageSample = null,
    ParentRemovalRequest? RemovalRequest = null,
    IReadOnlyList<DiagnosticReport>? Diagnostics = null);

public sealed record SessionAgentResponse(
    EnforcementState? Enforcement = null,
    DeviceRemovalResult? Removal = null);

/// <summary>
/// A message for the controlled user. <paramref name="IsUrgent"/> selects the Windows "urgent"
/// toast scenario, which stays on screen and breaks through Focus Assist; it is reserved for the
/// final warning before Windows signs the session out or closes an application. Everything else
/// - reminders, rule changes, availability, and completed updates - is an ordinary toast.
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
    DateTimeOffset? ScheduleAvailableAgainUtc);

public sealed record ApplicationTimeStatus(
    string IdentityKey,
    string DisplayName,
    bool IsManuallyBlocked,
    TimeAllowanceStatus Allowance);

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
/// </summary>
public sealed record EnforcementState(
    bool IsPcBlocked,
    RuleDecision PcDecision,
    RuleDecision? ForegroundApplicationDecision,
    int TodayActiveSeconds,
    int? DailyLimitSeconds,
    int RemainingSeconds,
    UserNotification? Notification,
    SessionStatusSnapshot? Status = null,
    AgentLanguage Language = AgentLanguage.English);
