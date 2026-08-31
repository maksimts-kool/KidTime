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
        // Extra time granted while the PC was blocked outright runs from the moment the parent
        // said yes, because a manual block and a closed schedule window have no seconds left in
        // them to add to. Until it runs out, those two are held off; the daily limit below is
        // not, since the same grant already raised it.
        var lifted = TimeBonus.LiftsBlocksAt(rule.Bonus, utcNow);

        if (!lifted && rule.ManuallyBlocked &&
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

        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        if (EffectiveDailyLimitSeconds(rule.DailyLimitSeconds, rule.Bonus, DateOnly.FromDateTime(localNow))
                is int limit
            && activeSecondsToday >= limit)
        {
            return new RuleDecision(
                false,
                BlockReason.DailyLimitReached,
                text.DeviceDailyLimitReached,
                StartOfNextLocalDayUtc(utcNow, timeZone));
        }

        if (!lifted && !rule.Schedule.Allows(localNow))
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
        var lifted = TimeBonus.LiftsBlocksAt(rule.Bonus, utcNow);

        if (!lifted && rule.ManuallyBlocked)
        {
            return new RuleDecision(
                false,
                BlockReason.ManualBlock,
                text.ApplicationBlockedByParent(rule.DisplayName));
        }

        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime;
        if (EffectiveDailyLimitSeconds(rule.DailyLimitSeconds, rule.Bonus, DateOnly.FromDateTime(localNow))
                is int limit
            && activeSecondsToday >= limit)
        {
            return new RuleDecision(
                false,
                BlockReason.DailyLimitReached,
                text.ApplicationDailyLimitReached(rule.DisplayName),
                StartOfNextLocalDayUtc(utcNow, timeZone));
        }

        if (!lifted && !rule.Schedule.Allows(localNow))
        {
            return new RuleDecision(
                false,
                BlockReason.OutsideAllowedSchedule,
                text.ApplicationOutsideSchedule(rule.DisplayName),
                FindNextAllowedUtc(rule.Schedule, utcNow, timeZone));
        }

        return RuleDecision.AllowedIn(text);
    }

    /// <summary>
    /// The daily limit that actually applies on a device-local date: the parent's limit plus any
    /// extra time granted for that date. Every path that reads a daily limit - enforcement, the
    /// running-out reminders, the child's own window - has to go through here, or the child is
    /// closed down at the original limit while being told they were given more.
    /// </summary>
    public static int? EffectiveDailyLimitSeconds(int? dailyLimitSeconds, TimeBonus? bonus, DateOnly localDate) =>
        dailyLimitSeconds is int limit ? limit + TimeBonus.SecondsOn(bonus, localDate) : null;

    /// <summary>
    /// Identifies the allowance period in force: the schedule window currently open, or the
    /// device-local day when no schedule is configured.
    ///
    /// This is what makes a refusal last exactly as long as it should. A child told no is told no
    /// for the stretch of screen time they are in - not for two minutes, and not for the rest of
    /// the week. When the next window opens, the key changes and they may ask again.
    /// </summary>
    public static string GetAllowancePeriodKey(
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId)
    {
        if (schedule.IsConfigured
            && FindCurrentAllowanceStartUtc(schedule, utcNow, timeZoneId) is { } windowStart)
            return $"window:{windowStart.UtcTicks}";
        return $"day:{GetLocalDate(utcNow, timeZoneId):yyyy-MM-dd}";
    }

    public static DateOnly GetLocalDate(DateTimeOffset utcNow, string timeZoneId) =>
        DateOnly.FromDateTime(ToLocalTime(utcNow, timeZoneId));

    /// <summary>
    /// An instant as the wall clock reads it on the controlled PC. The device reports a Windows
    /// timezone id ("Russian Standard Time"), which .NET resolves on any platform - so anything
    /// that has to speak in the child's clock time converts here rather than passing the id to
    /// something that only understands IANA names.
    /// </summary>
    public static DateTime ToLocalTime(DateTimeOffset utcNow, string timeZoneId) =>
        TimeZoneInfo.ConvertTime(utcNow, ResolveTimeZone(timeZoneId)).DateTime;

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
