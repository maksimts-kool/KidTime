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

/// <summary>
/// A stretch of device-local dates that runs on rules of its own - a holiday week with screen time
/// at 12:00-15:00 and 18:00-22:00 every day, instead of the school-week schedule.
///
/// Inside the dates, <see cref="Days"/> replaces the weekly schedule and
/// <see cref="DailyLimitSeconds"/> replaces the daily limit, both of them whole: a period with no
/// days is open at any time, and one with no limit has none, exactly as the regular rule reads.
/// Outside them nothing changes. It lives inside <see cref="WeeklySchedule"/> because
/// <see cref="WeeklySchedule.Allows"/> is the one question every schedule path asks - the
/// countdown, the allowance-end scan, the panel's "allowed until" - so each of them crosses the
/// first and last night of a period correctly without being taught about periods at all.
/// </summary>
public sealed class ScheduleOverride
{
    /// <summary>The panel's label for it, "Winter holidays". Never shown to the child.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>First device-local date the period applies to.</summary>
    public DateOnly StartDate { get; init; }

    /// <summary>Last device-local date the period applies to, inclusive.</summary>
    public DateOnly EndDate { get; init; }

    public List<DaySchedule> Days { get; init; } = [];
    public int? DailyLimitSeconds { get; init; }

    public bool Covers(DateOnly date) => date >= StartDate && date <= EndDate;
}

public sealed class WeeklySchedule
{
    public List<DaySchedule> Days { get; init; } = [];

    /// <summary>Date ranges that run on their own schedule and limit; see <see cref="ScheduleOverride"/>.</summary>
    public List<ScheduleOverride> Overrides { get; init; } = [];

    public bool IsConfigured => Days.Count > 0 || Overrides.Count > 0;

    /// <summary>The period in force on a device-local date, if any.</summary>
    public ScheduleOverride? OverrideOn(DateOnly date) =>
        Overrides.FirstOrDefault(period => period.Covers(date));

    /// <summary>The same week without its periods - what a parent edits as "the weekly schedule".</summary>
    public WeeklySchedule WithoutOverrides() => new() { Days = Days };

    public bool Allows(DateTime localTime)
    {
        if (!IsConfigured)
        {
            return true;
        }

        var date = DateOnly.FromDateTime(localTime);
        var week = DaysOn(date);
        // A date whose week has no days in it is not restricted - the regular week left empty, or
        // a period that sets only a limit - so nothing that started the night before matters.
        // A week that has days but not this one is closed today, as it always was.
        if (week.Count == 0)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(localTime);
        if (week.FirstOrDefault(day => day.Day == localTime.DayOfWeek)?.Windows.Any(window =>
                window.Contains(time) && (window.Start <= window.End || time >= window.Start)) == true)
        {
            return true;
        }

        // An overnight window belongs to the evening it started on, so it is read from whichever
        // schedule that date ran on: the last night of a holiday keeps its late window past
        // midnight, and the first night of one does not inherit the school night's.
        var previousDate = date.AddDays(-1);
        return DaysOn(previousDate).FirstOrDefault(day => day.Day == previousDate.DayOfWeek)?.Windows.Any(window =>
            window.Start > window.End && time < window.End) == true;
    }

    private List<DaySchedule> DaysOn(DateOnly date) => OverrideOn(date)?.Days ?? Days;
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

    /// <summary>
    /// Whether the child's screen-time window opens by itself when they sign in to Windows.
    ///
    /// A tray icon is something you have to know is there, and the window is where the child reads
    /// how much time they have, what the home network blocks, and how to ask for more minutes.
    /// Opening it once at sign-in shows them, without anything being enforced by it: the window
    /// can be closed straight away and nothing about the rules changes either way. It travels with
    /// the rules like the language does, so switching it on bumps the revision and reaches the PC
    /// over the ordinary path.
    /// </summary>
    public bool OpenWindowAtSignIn { get; init; }

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

/// <summary>
/// A restriction that has not started yet, and the seconds left before it does. Zero seconds
/// means it is in force now.
/// </summary>
public sealed record PendingRestriction(int Seconds, RuleDecision Decision);

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
