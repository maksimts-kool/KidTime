namespace KidTime.Domain.Rules;

public static class ScheduleValidator
{
    private const long DayTicks = TimeSpan.TicksPerDay;
    private const long WeekTicks = 7 * DayTicks;

    /// <summary>A special period is a holiday or a trip, not a second calendar.</summary>
    public const int MaximumOverrideDays = 366;

    /// <summary>
    /// Checks one special period against the ones already in force for the same PC or application.
    /// Two periods covering the same date would leave that date's rules to whichever happened to be
    /// read first, so they are refused rather than ordered.
    /// </summary>
    public static string? ValidateOverride(ScheduleOverride period, IEnumerable<ScheduleOverride> others)
    {
        if (string.IsNullOrWhiteSpace(period.Name)) return "Give the period a name.";
        if (period.EndDate < period.StartDate) return "The period must end on or after the day it starts.";
        if (period.EndDate.DayNumber - period.StartDate.DayNumber + 1 > MaximumOverrideDays)
            return $"A period can cover at most {MaximumOverrideDays} days.";
        if (period.DailyLimitSeconds is < 60 or > 86_400) return "Daily limit must be 60-86400 seconds.";
        if (Validate(new WeeklySchedule { Days = period.Days }) is { } scheduleError) return scheduleError;
        if (others.FirstOrDefault(other => other.StartDate <= period.EndDate && period.StartDate <= other.EndDate)
            is { } clash)
            return $"These dates overlap \"{clash.Name}\".";
        return null;
    }

    public static string? Validate(WeeklySchedule schedule)
    {
        var intervals = new List<Interval>();
        foreach (var day in schedule.Days)
        {
            foreach (var window in day.Windows)
            {
                var start = DayOffset(day.Day) + window.Start.Ticks;
                var end = window.Start == window.End
                    ? start + DayTicks
                    : DayOffset(day.Day) + window.End.Ticks + (window.Start > window.End ? DayTicks : 0);

                if (end <= WeekTicks)
                {
                    intervals.Add(new Interval(start, end));
                }
                else
                {
                    intervals.Add(new Interval(start, WeekTicks));
                    intervals.Add(new Interval(0, end - WeekTicks));
                }
            }
        }

        intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (var index = 1; index < intervals.Count; index++)
        {
            if (intervals[index].Start < intervals[index - 1].End)
                return "Schedule time ranges cannot overlap.";
        }

        return null;
    }

    private static long DayOffset(DayOfWeek day) =>
        ((7 + (int)day - (int)DayOfWeek.Monday) % 7) * DayTicks;

    private sealed record Interval(long Start, long End);
}
