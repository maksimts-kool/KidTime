using System.Reflection;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
using KidTime.Domain.Rules;

namespace KidTime.Domain.Tests;

public class AgentStringsTests
{
    public static TheoryData<AgentLanguage> AllLanguages()
    {
        var data = new TheoryData<AgentLanguage>();
        foreach (var language in Enum.GetValues<AgentLanguage>()) data.Add(language);
        return data;
    }

    /// <summary>
    /// The catalog is an abstract class precisely so that a missing translation cannot compile.
    /// This guards the other half of that promise: a member that compiles but returns nothing
    /// would leave a blank label on the child's screen.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryStringHasContentInEveryLanguage(AgentLanguage language)
    {
        var text = AgentStrings.For(language);
        var missing = new List<string>();

        foreach (var property in typeof(AgentStrings)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(item => item.PropertyType == typeof(string)))
        {
            if (string.IsNullOrWhiteSpace((string?)property.GetValue(text))) missing.Add(property.Name);
        }

        foreach (var method in typeof(AgentStrings)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(item => item.ReturnType == typeof(string) && !item.IsSpecialName))
        {
            var arguments = method.GetParameters().Select(SampleArgument).ToArray();
            if (arguments.Any(argument => argument is null)) continue;
            if (string.IsNullOrWhiteSpace((string?)method.Invoke(text, arguments))) missing.Add(method.Name);
        }

        Assert.Empty(missing);
    }

    private static object? SampleArgument(ParameterInfo parameter) => parameter.ParameterType switch
    {
        var type when type == typeof(string) => "Notepad",
        var type when type == typeof(int) => 90,
        var type when type == typeof(long) => 42L,
        var type when type == typeof(int?) => (int?)5_400,
        var type when type == typeof(bool) => true,
        var type when type == typeof(DateTimeOffset) => DateTimeOffset.UtcNow.AddHours(3),
        var type when type == typeof(IReadOnlyList<DnsFilterCategoryKind>) =>
            new[] { DnsFilterCategoryKind.Adult, DnsFilterCategoryKind.Gambling },
        _ => null
    };

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryStringMentionsTheApplicationItIsAbout(AgentLanguage language)
    {
        var text = AgentStrings.For(language);

        Assert.Contains("Minecraft", text.ApplicationBlockedByParent("Minecraft"), StringComparison.Ordinal);
        Assert.Contains("Minecraft", text.ApplicationDailyLimitReached("Minecraft"), StringComparison.Ordinal);
        Assert.Contains("Minecraft", text.ApplicationOutsideSchedule("Minecraft"), StringComparison.Ordinal);
        Assert.Contains("Minecraft", text.ApplicationClosingTitle("Minecraft", 60), StringComparison.Ordinal);
        Assert.Contains("Minecraft", text.ApplicationTimeLeftTitle("Minecraft", 300), StringComparison.Ordinal);
        Assert.Contains("Minecraft", text.ApplicationTimeLeftMessage("Minecraft"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void CountdownsAndDurationsCarryTheNumber(AgentLanguage language)
    {
        var text = AgentStrings.For(language);

        Assert.StartsWith("45 ", text.Countdown(45), StringComparison.Ordinal);
        Assert.StartsWith("1 ", text.Countdown(60), StringComparison.Ordinal);
        Assert.StartsWith("2 ", text.Countdown(90), StringComparison.Ordinal);
        Assert.StartsWith("5 ", text.Countdown(300), StringComparison.Ordinal);
        Assert.Contains("45", text.DurationLabel(45 * 60), StringComparison.Ordinal);
        Assert.Contains("2", text.DurationLabel(2 * 3600 + 5 * 60), StringComparison.Ordinal);
        Assert.Contains("05", text.DurationLabel(2 * 3600 + 5 * 60), StringComparison.Ordinal);
        Assert.Contains("3", text.LimitText(3 * 3600), StringComparison.Ordinal);
        Assert.Equal(text.NoLimit, text.LimitText(null));
    }

    /// <summary>
    /// Russian picks one of three forms by the count, and an urgent toast that says
    /// "через 2 минут" reads as broken to the child it is trying to warn.
    /// </summary>
    [Theory]
    [InlineData(60, "1 минуту")]
    [InlineData(120, "2 минуты")]
    [InlineData(300, "5 минут")]
    [InlineData(660, "11 минут")]
    [InlineData(1_320, "22 минуты")]
    [InlineData(21, "21 секунду")]
    [InlineData(23, "23 секунды")]
    [InlineData(25, "25 секунд")]
    [InlineData(11, "11 секунд")]
    public void RussianSignOutTitleAgreesWithTheCount(int seconds, string expected)
    {
        Assert.Contains(expected, AgentStrings.Russian.SignOutCountdownTitle(seconds), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(60, "1 минута")]
    [InlineData(120, "2 минуты")]
    [InlineData(300, "5 минут")]
    public void RussianTimeLeftWarningAgreesWithTheCount(int seconds, string expected)
    {
        Assert.Contains(expected, AgentStrings.Russian.PcTimeLeftTitle(seconds), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        Assert.Same(AgentStrings.English, AgentStrings.For((AgentLanguage)999));
    }

    [Fact]
    public void DeviceRulesAreEvaluatedInTheConfiguredLanguage()
    {
        var rule = new DeviceRuleSnapshot
        {
            Language = AgentLanguage.Russian,
            ManuallyBlocked = true
        };

        var decision = RuleEvaluator.EvaluateDevice(rule, DateTimeOffset.UtcNow, 0);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AgentStrings.Russian.DeviceBlockedByParent, decision.Message);
    }

    [Fact]
    public void ApplicationRulesAreEvaluatedInTheConfiguredLanguage()
    {
        var rule = new ApplicationRuleSnapshot
        {
            IdentityKey = "app",
            DisplayName = "Minecraft",
            ManuallyBlocked = true
        };

        var russian = RuleEvaluator.EvaluateApplication(
            rule, DateTimeOffset.UtcNow, "UTC", 0, AgentLanguage.Russian);
        var english = RuleEvaluator.EvaluateApplication(rule, DateTimeOffset.UtcNow, "UTC", 0);

        Assert.Equal(AgentStrings.Russian.ApplicationBlockedByParent("Minecraft"), russian.Message);
        Assert.Equal(AgentStrings.English.ApplicationBlockedByParent("Minecraft"), english.Message);
    }

    [Fact]
    public void SnapshotsDefaultToEnglishSoAnUnsetDeviceStillReads()
    {
        Assert.Equal(AgentLanguage.English, new DeviceRuleSnapshot().Language);
    }

    [Fact]
    public void LimitChangeMessageNamesOnlyWhatActuallyChanged()
    {
        var text = AgentStrings.English;

        var both = text.LimitChangeMessage("PC", 7_200, dailyChanged: true, scheduleChanged: true);
        var scheduleOnly = text.LimitChangeMessage("PC", 7_200, dailyChanged: false, scheduleChanged: true);

        Assert.Contains("2h", both, StringComparison.Ordinal);
        Assert.Contains("schedule", both, StringComparison.Ordinal);
        Assert.DoesNotContain("2h", scheduleOnly, StringComparison.Ordinal);
        Assert.Contains("schedule", scheduleOnly, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence a child reads when a page would not open. It has to name what the household
    /// blocks and what is shut right now - and it must not be written as if it knew which site was
    /// asked for, because nothing in KidTime does.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void TheWebFilterExplanationNamesTheRulesAndNotTheSite(AgentLanguage language)
    {
        var text = AgentStrings.For(language);

        var full = text.WebFilterBlockedMessage(
            [DnsFilterCategoryKind.Adult, DnsFilterCategoryKind.Gambling], "Roblox", "18:00");
        Assert.Contains("Roblox", full, StringComparison.Ordinal);
        Assert.Contains("18:00", full, StringComparison.Ordinal);

        // Nothing shut on a timetable: the categories alone still have to make a sentence.
        var categoriesOnly = text.WebFilterBlockedMessage([DnsFilterCategoryKind.Malware], null, null);
        Assert.False(string.IsNullOrWhiteSpace(categoriesOnly));
        Assert.DoesNotContain("Roblox", categoriesOnly, StringComparison.Ordinal);

        // A set shut until a parent says otherwise is named without a time attached to it.
        var noDeadline = text.WebFilterBlockedMessage([], "Roblox", null);
        Assert.Contains("Roblox", noDeadline, StringComparison.Ordinal);
        Assert.DoesNotContain("18:00", noDeadline, StringComparison.Ordinal);
    }
}
