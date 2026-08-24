using KidTime.Domain.Rules;

namespace KidTime.Domain.Tests;

public sealed class RuleEvaluatorTests
{
    private static readonly DateTimeOffset MondayNoonUtc = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Device_is_blocked_when_daily_limit_is_exhausted()
    {
        var rule = NewDeviceRule(dailyLimitSeconds: 3600);

        var result = RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 3600);

        Assert.False(result.IsAllowed);
        Assert.Equal(BlockReason.DailyLimitReached, result.Reason);
        Assert.NotNull(result.AvailableAtUtc);
    }

    [Fact]
    public void Active_time_below_daily_limit_is_allowed()
    {
        var result = RuleEvaluator.EvaluateDevice(NewDeviceRule(3600), MondayNoonUtc, 3599);

        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Manual_block_takes_precedence()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            ManuallyBlocked = true
        };

        var result = RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 0);

        Assert.Equal(BlockReason.ManualBlock, result.Reason);
    }

    [Fact]
    public void Expired_temporary_manual_block_is_ignored()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            ManuallyBlocked = true,
            ManualBlockUntilUtc = MondayNoonUtc.AddMinutes(-1)
        };

        Assert.True(RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 0).IsAllowed);
    }

    [Fact]
    public void Weekly_schedule_blocks_outside_window()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            Schedule = Schedule((DayOfWeek.Monday, "15:00", "21:00"))
        };

        var result = RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 0);

        Assert.Equal(BlockReason.OutsideAllowedSchedule, result.Reason);
    }

    [Fact]
    public void Weekly_schedule_allows_inside_window()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            Schedule = Schedule((DayOfWeek.Monday, "11:00", "13:00"))
        };

        Assert.True(RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 0).IsAllowed);
    }

    [Fact]
    public void Schedule_window_can_cross_midnight()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            Schedule = Schedule((DayOfWeek.Monday, "22:00", "02:00"))
        };
        var tuesdayAtOne = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);

        Assert.True(RuleEvaluator.EvaluateDevice(rule, tuesdayAtOne, 0).IsAllowed);
    }

    [Fact]
    public void Multiple_schedule_windows_on_the_same_day_are_allowed()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            Schedule = Schedule(
                (DayOfWeek.Monday, "12:00", "15:00"),
                (DayOfWeek.Monday, "18:00", "20:00"))
        };

        Assert.True(RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc, 0).IsAllowed);
        Assert.False(RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc.AddHours(4), 0).IsAllowed);
        Assert.True(RuleEvaluator.EvaluateDevice(rule, MondayNoonUtc.AddHours(6), 0).IsAllowed);
        Assert.Null(ScheduleValidator.Validate(rule.Schedule));
    }

    [Fact]
    public void Overlapping_schedule_windows_are_rejected()
    {
        var schedule = Schedule(
            (DayOfWeek.Monday, "12:00", "15:00"),
            (DayOfWeek.Monday, "14:30", "18:00"));

        Assert.NotNull(ScheduleValidator.Validate(schedule));
    }

    [Fact]
    public void Overnight_window_cannot_overlap_the_following_day()
    {
        var schedule = Schedule(
            (DayOfWeek.Monday, "22:00", "02:00"),
            (DayOfWeek.Tuesday, "01:00", "03:00"));

        Assert.NotNull(ScheduleValidator.Validate(schedule));
    }

    [Fact]
    public void Adjacent_schedule_windows_do_not_overlap()
    {
        var schedule = Schedule(
            (DayOfWeek.Monday, "12:00", "15:00"),
            (DayOfWeek.Monday, "15:00", "18:00"));

        Assert.Null(ScheduleValidator.Validate(schedule));
    }

    [Fact]
    public void Application_rule_combines_block_limit_and_schedule()
    {
        var rule = new ApplicationRuleSnapshot
        {
            IdentityKey = "roblox",
            DisplayName = "Roblox",
            DailyLimitSeconds = 120,
            Schedule = Schedule((DayOfWeek.Monday, "11:00", "21:00"))
        };

        var result = RuleEvaluator.EvaluateApplication(rule, MondayNoonUtc, "UTC", 120);

        Assert.Equal(BlockReason.DailyLimitReached, result.Reason);
    }

    [Fact]
    public void Local_date_uses_device_timezone()
    {
        var utc = new DateTimeOffset(2026, 8, 24, 22, 30, 0, TimeSpan.Zero);

        var date = RuleEvaluator.GetLocalDate(utc, "FLE Standard Time");

        Assert.Equal(new DateOnly(2026, 8, 25), date);
    }

    [Fact]
    public void Current_schedule_allowance_reports_when_access_ends()
    {
        var schedule = Schedule((DayOfWeek.Monday, "11:00", "13:00"));

        var result = RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, MondayNoonUtc, "UTC");

        Assert.Equal(new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void Next_schedule_allowance_is_a_stable_minute_boundary()
    {
        var rule = new DeviceRuleSnapshot
        {
            TimeZoneId = "UTC",
            Schedule = Schedule((DayOfWeek.Monday, "13:00", "14:00"))
        };
        var firstEvaluation = MondayNoonUtc.AddSeconds(17);
        var secondEvaluation = MondayNoonUtc.AddSeconds(18);

        var first = RuleEvaluator.EvaluateDevice(rule, firstEvaluation, 0);
        var second = RuleEvaluator.EvaluateDevice(rule, secondEvaluation, 0);

        var expected = new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, first.AvailableAtUtc);
        Assert.Equal(expected, second.AvailableAtUtc);
    }

    [Fact]
    public void Schedule_status_reports_current_and_next_allowance_separately()
    {
        var schedule = Schedule((DayOfWeek.Monday, "13:00", "14:00"));

        Assert.False(RuleEvaluator.IsWithinSchedule(schedule, MondayNoonUtc, "UTC"));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero),
            RuleEvaluator.FindNextAllowanceStartUtc(schedule, MondayNoonUtc, "UTC"));
        Assert.True(RuleEvaluator.IsWithinSchedule(schedule, MondayNoonUtc.AddHours(1), "UTC"));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero),
            RuleEvaluator.FindCurrentAllowanceStartUtc(schedule, MondayNoonUtc.AddHours(1), "UTC"));
    }

    [Fact]
    public void Overnight_allowance_reports_the_next_day_end()
    {
        var schedule = Schedule((DayOfWeek.Monday, "22:00", "02:00"));
        var tuesdayAtOne = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);

        var result = RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, tuesdayAtOne, "UTC");

        Assert.Equal(new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero), result);
    }

    private static DeviceRuleSnapshot NewDeviceRule(int? dailyLimitSeconds = null) => new()
    {
        TimeZoneId = "UTC",
        DailyLimitSeconds = dailyLimitSeconds
    };

    private static WeeklySchedule Schedule(params (DayOfWeek Day, string Start, string End)[] windows) => new()
    {
        Days = windows.GroupBy(window => window.Day).Select(group => new DaySchedule
        {
            Day = group.Key,
            Windows = group.Select(window => new TimeWindow(
                TimeOnly.Parse(window.Start),
                TimeOnly.Parse(window.End))).ToList()
        }).ToList()
    };
}
