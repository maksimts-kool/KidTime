using KidTime.Domain.Localization;

namespace KidTime.Domain.Rules;

public static class RuleEvaluator
{
    public static RuleDecision EvaluateDevice(
        DeviceRuleSnapshot rule,
        DateTimeOffset utcNow,
        int activeSecondsToday)
    {
        var timeZone = ResolveTimeZone(rule.TimeZoneId);
        var text = AgentStrings.For(rule.Language);

        if (rule.ManuallyBlocked &&
            (rule.ManualBlockUntilUtc is null || rule.ManualBlockUntilUtc > utcNow))
        {
            return new RuleDecision(
                false,
                BlockReason.ManualBlock,
                rule.ManualBlockUntilUtc is null
                    ? text.DeviceBlockedByParent
                    : text.DeviceTemporarilyBlocked,
                rule.ManualBlockUntilUtc);
        }

        if (rule.DailyLimitSeconds is int limit && activeSecondsToday >= limit)
        {
            return new RuleDecision(
                false,
                BlockReason.DailyLimitReached,
                text.DeviceDailyLimitReached,
                StartOfNextLocalDayUtc(utcNow, timeZone));
        }

        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        if (!rule.Schedule.Allows(localNow))
        {
            return new RuleDecision(
                false,
                BlockReason.OutsideAllowedSchedule,
                text.DeviceOutsideSchedule,
                FindNextAllowedUtc(rule.Schedule, utcNow, timeZone));
        }

        return RuleDecision.AllowedIn(text);
    }

    public static RuleDecision EvaluateApplication(
        ApplicationRuleSnapshot rule,
        DateTimeOffset utcNow,
        string timeZoneId,
        int activeSecondsToday,
        AgentLanguage language = AgentLanguage.English)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var text = AgentStrings.For(language);

        if (rule.ManuallyBlocked)
        {
            return new RuleDecision(
                false,
                BlockReason.ManualBlock,
                text.ApplicationBlockedByParent(rule.DisplayName));
        }

        if (rule.DailyLimitSeconds is int limit && activeSecondsToday >= limit)
        {
            return new RuleDecision(
                false,
                BlockReason.DailyLimitReached,
                text.ApplicationDailyLimitReached(rule.DisplayName),
                StartOfNextLocalDayUtc(utcNow, timeZone));
        }

        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        if (!rule.Schedule.Allows(localNow))
        {
            return new RuleDecision(
                false,
                BlockReason.OutsideAllowedSchedule,
                text.ApplicationOutsideSchedule(rule.DisplayName),
                FindNextAllowedUtc(rule.Schedule, utcNow, timeZone));
        }

        return RuleDecision.AllowedIn(text);
    }

    public static DateOnly GetLocalDate(DateTimeOffset utcNow, string timeZoneId)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, ResolveTimeZone(timeZoneId));
        return DateOnly.FromDateTime(localNow.DateTime);
    }

    public static DateTimeOffset? FindCurrentAllowanceEndUtc(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId)
    {
        if (!schedule.IsConfigured)
        {
            return null;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        if (!schedule.Allows(TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime))
        {
            return null;
        }

        var firstMinuteBoundary = new DateTimeOffset(
            utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero).AddMinutes(1);
        for (var minutes = 0; minutes <= 8 * 24 * 60; minutes++)
        {
            var candidate = firstMinuteBoundary.AddMinutes(minutes);
            if (!schedule.Allows(TimeZoneInfo.ConvertTime(candidate, timeZone).DateTime))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsWithinSchedule(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId) =>
        !schedule.IsConfigured
        || schedule.Allows(TimeZoneInfo.ConvertTime(utcNow, ResolveTimeZone(timeZoneId)).DateTime);

    public static DateTimeOffset? FindCurrentAllowanceStartUtc(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId)
    {
        if (!schedule.IsConfigured || !IsWithinSchedule(schedule, utcNow, timeZoneId))
        {
            return null;
        }

        var timeZone = ResolveTimeZone(timeZoneId);
        var currentMinute = new DateTimeOffset(
            utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero);
        for (var minutes = 0; minutes <= 8 * 24 * 60; minutes++)
        {
            var candidate = currentMinute.AddMinutes(-minutes);
            if (!schedule.Allows(TimeZoneInfo.ConvertTime(candidate, timeZone).DateTime))
            {
                return candidate.AddMinutes(1);
            }
        }

        return null;
    }

    public static DateTimeOffset? FindNextAllowanceStartUtc(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId)
    {
        if (!schedule.IsConfigured)
        {
            return null;
        }

        return FindNextAllowedUtc(schedule, utcNow, ResolveTimeZone(timeZoneId));
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static DateTimeOffset StartOfNextLocalDayUtc(DateTimeOffset utcNow, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var nextLocalMidnight = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, timeZone), TimeSpan.Zero);
    }

    private static DateTimeOffset? FindNextAllowedUtc(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        TimeZoneInfo timeZone)
    {
        var firstMinuteBoundary = new DateTimeOffset(
            utcNow.Year, utcNow.Month, utcNow.Day, utcNow.Hour, utcNow.Minute, 0, TimeSpan.Zero).AddMinutes(1);
        for (var minutes = 0; minutes <= 8 * 24 * 60; minutes++)
        {
            var candidate = firstMinuteBoundary.AddMinutes(minutes);
            var localCandidate = TimeZoneInfo.ConvertTime(candidate, timeZone).DateTime;
            if (schedule.Allows(localCandidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
