using KidTime.Server.Services;

namespace KidTime.Server.Tests;

/// <summary>
/// How approved grants become the bonus that raises a limit and lifts a block. The snapshot the PC
/// enforces and the panel's device and application lists both read it from here, so a card never
/// says "limit reached" about a scope the parent has just given more time.
/// </summary>
public sealed class GrantBonusTests
{
    private static readonly DateOnly Today = new(2026, 10, 26);
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);

    private static RuleSnapshotFactory.GrantRow Grant(DateOnly localDate, int minutes, DateTimeOffset decidedAt) => new()
    {
        LocalDate = localDate,
        GrantedMinutes = minutes,
        DecidedAtUtc = decidedAt
    };

    [Fact]
    public void NoGrantsMeansNoBonus()
    {
        Assert.Null(RuleSnapshotFactory.BonusFor([], Today, Now));
    }

    [Fact]
    public void TodaysGrantsAddUpAndTheLatestOpenWindowWins()
    {
        var bonus = RuleSnapshotFactory.BonusFor(
            [Grant(Today, 15, Now.AddMinutes(-5)), Grant(Today, 20, Now.AddMinutes(-2))],
            Today,
            Now);

        Assert.NotNull(bonus);
        Assert.Equal(35 * 60, bonus.Seconds);
        Assert.Equal(Now.AddMinutes(18), bonus.LiftedUntilUtc);
    }

    [Fact]
    public void YesterdaysGrantKeepsItsWindowButAddsNoMinutesToToday()
    {
        var bonus = RuleSnapshotFactory.BonusFor([Grant(Today.AddDays(-1), 30, Now.AddMinutes(-10))], Today, Now);

        Assert.NotNull(bonus);
        Assert.Equal(0, bonus.Seconds);
        Assert.Equal(Now.AddMinutes(20), bonus.LiftedUntilUtc);
    }

    [Fact]
    public void AnOlderRequestApprovedLateNeitherRaisesTheLimitNorOpensAWindow()
    {
        Assert.Null(RuleSnapshotFactory.BonusFor([Grant(Today.AddDays(-2), 30, Now.AddMinutes(-1))], Today, Now));
    }

    [Fact]
    public void AnExpiredWindowStillLeavesTodaysMinutes()
    {
        var bonus = RuleSnapshotFactory.BonusFor([Grant(Today, 10, Now.AddHours(-2))], Today, Now);

        Assert.NotNull(bonus);
        Assert.Equal(600, bonus.Seconds);
        Assert.Null(bonus.LiftedUntilUtc);
    }
}
