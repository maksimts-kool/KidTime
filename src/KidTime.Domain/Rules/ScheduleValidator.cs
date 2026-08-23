namespace KidTime.Domain.Rules;

public static class ScheduleValidator
{
    private const long DayTicks = TimeSpan.TicksPerDay;
    private const long WeekTicks = 7 * DayTicks;

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
