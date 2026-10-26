using KidTime.Domain.Localization;

namespace KidTime.Server.Data;

public sealed class ParentUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public required string PasswordHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string WindowsVersion { get; set; }
    public required string TimeZoneId { get; set; }
    public DateTimeOffset EnrolledAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? LoggedInUser { get; set; }
    public string? ForegroundApplication { get; set; }
    public string? ForegroundIdentityKey { get; set; }
    public string WindowsUsersJson { get; set; } = "[]";
    public string? AgentVersion { get; set; }
    public string? AgentUpdateStatus { get; set; }
    public string? AgentUpdateError { get; set; }
    public DateTimeOffset? AgentUpdateCheckedAtUtc { get; set; }
    public long AppliedRuleRevision { get; set; }
    public DeviceRule Rule { get; set; } = null!;
    public List<DeviceCredential> Credentials { get; set; } = [];
    public List<DeviceApplication> Applications { get; set; } = [];
}

public sealed class DeviceCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class EnrollmentToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? UsedAtUtc { get; set; }
    public Guid? EnrolledDeviceId { get; set; }
}

public sealed class DeviceRule
{
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public long Revision { get; set; } = 1;
    public string? ControlledUserSid { get; set; }
    public string? ControlledUserName { get; set; }
    public int IdleThresholdSeconds { get; set; } = 300;

    /// <summary>
    /// The language the controlled PC speaks to the child in. It rides with the rules, so a
    /// change increments the revision and reaches the agent over the usual path.
    /// </summary>
    public AgentLanguage Language { get; set; } = AgentLanguage.English;

    /// <summary>
    /// Whether the child's screen-time window opens by itself when they sign in to Windows. Like
    /// the language, it rides with the rules, so a change increments the revision and reaches the
    /// PC over the usual path. It enforces nothing.
    /// </summary>
    public bool OpenWindowAtSignIn { get; set; }

    public bool ManuallyBlocked { get; set; }
    public DateTimeOffset? ManualBlockUntilUtc { get; set; }
    public int? DailyLimitSeconds { get; set; } = 18_000;
    public string ScheduleJson { get; set; } = "{\"days\":[]}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Application
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string IdentityKey { get; set; }
    public required string DisplayName { get; set; }
    public required string ExecutableName { get; set; }
    public string? ProductName { get; set; }
    public string? OriginalFilename { get; set; }
    public string? Company { get; set; }
    public string? SignaturePublisher { get; set; }
    public string? PackageFamilyName { get; set; }
    public List<DeviceApplication> Devices { get; set; } = [];
}

public sealed class DeviceApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public Guid ApplicationId { get; set; }
    public Application Application { get; set; } = null!;
    public required string ExecutablePath { get; set; }
    public string? FileVersion { get; set; }
    public string? Sha256 { get; set; }
    public byte[]? IconPng { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public ApplicationRule Rule { get; set; } = null!;
}

public sealed class ApplicationRule
{
    public Guid DeviceApplicationId { get; set; }
    public DeviceApplication DeviceApplication { get; set; } = null!;
    public bool ManuallyBlocked { get; set; }
    public int? DailyLimitSeconds { get; set; }
    public string ScheduleJson { get; set; } = "{\"days\":[]}";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DailyDeviceUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public DateOnly LocalDate { get; set; }
    public int ActiveSeconds { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DailyApplicationUsage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceApplicationId { get; set; }
    public DeviceApplication DeviceApplication { get; set; } = null!;
    public DateOnly LocalDate { get; set; }
    public int ActiveSeconds { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One fault reported by an enrolled PC, collapsed by fingerprint so a bug that repeats stays
/// one row with a count. This is the only way a parent sees what went wrong on a device they
/// usually cannot reach.
/// </summary>
public sealed class DeviceDiagnosticEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public Guid LastReportId { get; set; }
    public required string Fingerprint { get; set; }
    public required string Component { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
    public string? ExceptionType { get; set; }
    public string? Detail { get; set; }
    public string? AgentVersion { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset FirstOccurredAtUtc { get; set; }
    public DateTimeOffset LastOccurredAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; set; }
}

public sealed class ProcessedUsageBatch
{
    public Guid BatchId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DeviceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public required string Type { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcknowledgedAtUtc { get; set; }
}

/// <summary>
/// One request from a controlled PC for extra time, and what the parent decided about it.
///
/// <see cref="Id"/> is the id the agent minted, so an upload retried after an uncertain response
/// lands on the row that already exists rather than asking the parent the same question twice.
/// An approved row is read back into the rule snapshot as a <c>TimeBonus</c> for
/// <see cref="LocalDate"/>, which is what actually extends the limit - and what makes the grant
/// expire on its own when that date passes.
/// </summary>
public sealed class TimeExtension
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    /// <summary>Null for the PC's own screen time; otherwise the application that is running out.</summary>
    public Guid? DeviceApplicationId { get; set; }
    public DeviceApplication? DeviceApplication { get; set; }
    public string? ApplicationIdentityKey { get; set; }

    /// <summary>The name the child saw when they asked, kept so the panel reads the same.</summary>
    public required string DisplayName { get; set; }

    public DateOnly LocalDate { get; set; }
    public int RequestedMinutes { get; set; }
    public int GrantedMinutes { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedByParentUserId { get; set; }

    /// <summary>
    /// Extra time the parent gave from the panel without being asked. It is stored as an approved
    /// request for zero minutes so the snapshot, the agent's announcement and the panel's history
    /// all read it exactly like an answered one, and it is left out of the child's daily cap -
    /// that cap bounds how often the child may ask, not how often a parent may give.
    /// </summary>
    public bool InitiatedByParent { get; set; }
}

/// <summary>The three states a request can be in, as they are stored and sent.</summary>
public static class TimeExtensionStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Denied = "Denied";
}

/// <summary>
/// A stretch of dates on which a PC or one application runs on a schedule and daily limit of its
/// own - a holiday week, a trip, exam season. It reaches the agent inside the rule snapshot as a
/// <c>ScheduleOverride</c> on the schedule it replaces, so the PC carries its upcoming periods
/// offline and switches over at midnight on its own clock with nothing sent at the boundary.
/// </summary>
public sealed class SpecialPeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    /// <summary>Null for the PC's own screen time; otherwise the application it applies to.</summary>
    public Guid? DeviceApplicationId { get; set; }
    public DeviceApplication? DeviceApplication { get; set; }

    public required string Name { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>The days of a <c>WeeklySchedule</c>; empty means no schedule during the period.</summary>
    public string ScheduleJson { get; set; } = "{\"days\":[]}";

    /// <summary>Replaces the regular daily limit for the period; null means no limit at all.</summary>
    public int? DailyLimitSeconds { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
