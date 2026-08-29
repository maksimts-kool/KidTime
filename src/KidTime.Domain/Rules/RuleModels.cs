using KidTime.Domain.Localization;

namespace KidTime.Domain.Rules;

public sealed record TimeWindow(TimeOnly Start, TimeOnly End)
{
    public bool Contains(TimeOnly value)
    {
        if (Start == End)
        {
            return true;
        }

        return Start < End
            ? value >= Start && value < End
            : value >= Start || value < End;
    }
}

public sealed class DaySchedule
{
    public DayOfWeek Day { get; init; }
    public List<TimeWindow> Windows { get; init; } = [];
}

public sealed class WeeklySchedule
{
    public List<DaySchedule> Days { get; init; } = [];

    public bool IsConfigured => Days.Count > 0;

    public bool Allows(DateTime localTime)
    {
        if (!IsConfigured)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(localTime);
        var today = Days.FirstOrDefault(day => day.Day == localTime.DayOfWeek);
        if (today?.Windows.Any(window => window.Contains(time) &&
                (window.Start <= window.End || time >= window.Start)) == true)
        {
            return true;
        }

        var previousDay = localTime.AddDays(-1).DayOfWeek;
        return Days.FirstOrDefault(day => day.Day == previousDay)?.Windows.Any(window =>
            window.Start > window.End && time < window.End) == true;
    }
}

/// <summary>
/// Extra time a parent granted for one local date, in answer to a child asking for it.
///
/// It rides inside the rule snapshot rather than on a channel of its own, so granting it bumps
/// the rule revision and reaches the PC over the path that already exists - and it expires by
/// itself the moment the device-local date moves on, with no second message needed to take it
/// back.
///
/// It carries two things, because the restrictions extra time can lift are not counted the same
/// way. <paramref name="Seconds"/> raises a daily limit and is spent in active foreground time
/// like the rest of that allowance - and means nothing beside an unlimited one.
/// <paramref name="LiftedUntilUtc"/> is what a grant does to a manual block or a closed schedule
/// window: neither is an allowance with seconds left in it, so the grant runs as wall-clock time
/// from the moment the parent approved it, which is the one starting point the child and the
/// parent both saw.
/// </summary>
public sealed record TimeBonus(DateOnly LocalDate, int Seconds, DateTimeOffset? LiftedUntilUtc = null)
{
    /// <summary>The bonus that applies on a given device-local date, in seconds; zero otherwise.</summary>
    public static int SecondsOn(TimeBonus? bonus, DateOnly localDate) =>
        bonus is not null && bonus.LocalDate == localDate ? Math.Max(0, bonus.Seconds) : 0;

    /// <summary>
    /// Whether a grant is still holding a manual block or a schedule off. Deliberately not scoped
    /// to a local date: a window opened at ten to midnight runs the minutes it was given.
    /// </summary>
    public static bool LiftsBlocksAt(TimeBonus? bonus, DateTimeOffset utcNow) =>
        bonus?.LiftedUntilUtc is DateTimeOffset until && until > utcNow;
}

public sealed class ApplicationRuleSnapshot
{
    public required string IdentityKey { get; init; }
    public string DisplayName { get; init; } = "Application";
    public bool ManuallyBlocked { get; init; }
    public int? DailyLimitSeconds { get; init; }
    public WeeklySchedule Schedule { get; init; } = new();

    /// <summary>Extra time granted for this application today, if any.</summary>
    public TimeBonus? Bonus { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed class DeviceRuleSnapshot
{
    public Guid DeviceId { get; init; }
    public long Revision { get; init; }
    public string TimeZoneId { get; init; } = "UTC";

    /// <summary>
    /// The language every message on the controlled PC is written in. It travels with the rules
    /// so that changing it bumps the revision and reaches the agent over the same path.
    /// </summary>
    public AgentLanguage Language { get; init; } = AgentLanguage.English;

    public string? ControlledUserSid { get; init; }
    public string? ControlledUserName { get; init; }
    public int IdleThresholdSeconds { get; init; } = 300;
    public bool ManuallyBlocked { get; init; }
    public DateTimeOffset? ManualBlockUntilUtc { get; init; }
    public int? DailyLimitSeconds { get; init; }
    public WeeklySchedule Schedule { get; init; } = new();

    /// <summary>Extra screen time granted for this PC today, if any.</summary>
    public TimeBonus? Bonus { get; init; }

    public List<ApplicationRuleSnapshot> Applications { get; init; } = [];
}

public enum BlockReason
{
    None,
    ManualBlock,
    DailyLimitReached,
    OutsideAllowedSchedule
}

public sealed record RuleDecision(
    bool IsAllowed,
    BlockReason Reason,
    string Message,
    DateTimeOffset? AvailableAtUtc = null)
{
    public static RuleDecision Allowed { get; } = new(true, BlockReason.None, AgentStrings.English.Allowed);

    public static RuleDecision AllowedIn(AgentStrings text) =>
        new(true, BlockReason.None, text.Allowed);
}
