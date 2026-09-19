using KidTime.Domain.Contracts;

namespace KidTime.Domain.Tests;

public class DnsFilterPolicyTests
{
    private static readonly TimeZoneInfo Tallinn = TimeZoneInfo.FindSystemTimeZoneById("Europe/Tallinn");

    private static DateTimeOffset Local(string localTime)
    {
        var moment = DateTime.Parse(localTime, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(moment, Tallinn.GetUtcOffset(moment)).ToUniversalTime();
    }

    /// <summary>
    /// AdGuard serves every list it registers from the same host, so a gambling list and an ad
    /// list have identical addresses but for a number. Reading the publisher's name first labelled
    /// a real household's gambling, adult and tracker lists all as "Ads".
    /// </summary>
    [Theory]
    [InlineData("https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt", DnsFilterCategoryKind.Ads)]
    [InlineData("https://adguardteam.github.io/HostlistsRegistry/assets/filter_2.txt", DnsFilterCategoryKind.Ads)]
    [InlineData("https://adguardteam.github.io/HostlistsRegistry/assets/filter_47.txt", DnsFilterCategoryKind.Gambling)]
    [InlineData("https://adguardteam.github.io/HostlistsRegistry/assets/filter_63.txt", DnsFilterCategoryKind.Trackers)]
    public void RegistryNumbersAreReadBeforeThePublisherName(string url, DnsFilterCategoryKind expected) =>
        Assert.Equal(expected, DnsFilterPolicy.ClassifyList(url));

    /// <summary>
    /// The list that blocks malware and phishing is called <c>tif.txt</c> and lives in a directory
    /// called <c>adblock</c>. The file name is the narrower evidence and has to win.
    /// </summary>
    [Theory]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/tif.txt", DnsFilterCategoryKind.Malware)]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/tif.medium.txt", DnsFilterCategoryKind.Malware)]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/nsfw.txt", DnsFilterCategoryKind.Adult)]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/gambling.txt", DnsFilterCategoryKind.Gambling)]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/native.winoffice.txt", DnsFilterCategoryKind.Trackers)]
    [InlineData("https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/pro.txt", DnsFilterCategoryKind.Ads)]
    public void TheFileNameOutweighsTheDirectory(string url, DnsFilterCategoryKind expected) =>
        Assert.Equal(expected, DnsFilterPolicy.ClassifyList(url));

    /// <summary>A label a child reads has to be right; an address that says nothing gets no guess.</summary>
    [Theory]
    [InlineData("https://example.org/lists/custom.txt")]
    [InlineData("https://adguardteam.github.io/HostlistsRegistry/assets/filter_9999.txt")]
    [InlineData("")]
    public void AnAddressThatSaysNothingIsNotGuessedAt(string url) =>
        Assert.Equal(DnsFilterCategoryKind.Other, DnsFilterPolicy.ClassifyList(url));

    [Fact]
    public void CategoriesAreCountedOnceAndOrderedBySeverity()
    {
        var summary = DnsFilterPolicy.Summarize(
        [
            "https://adguardteam.github.io/HostlistsRegistry/assets/filter_1.txt",
            "https://adguardteam.github.io/HostlistsRegistry/assets/filter_2.txt",
            // The same address twice is one list, however the parent typed it.
            "https://ADGUARDTEAM.github.io/HostlistsRegistry/assets/filter_2.txt",
            "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/tif.txt",
            "https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/nsfw.txt",
            "   "
        ]);

        Assert.Equal(
            [DnsFilterCategoryKind.Malware, DnsFilterCategoryKind.Adult, DnsFilterCategoryKind.Ads],
            summary.Select(item => item.Kind));
        Assert.Equal(2, summary.Single(item => item.Kind == DnsFilterCategoryKind.Ads).ListCount);
    }

    /// <summary>
    /// The household's real Roblox timetable: closed overnight from 22:00 until noon, and again
    /// between 15:00 and 18:00. Every answer also names the instant it turns over, because "back
    /// at 12:00" is the only part of it a child can act on.
    /// </summary>
    [Theory]
    [InlineData("2026-09-19T10:00", true, "2026-09-19T12:00")]
    [InlineData("2026-09-19T13:00", false, "2026-09-19T15:00")]
    [InlineData("2026-09-19T16:00", true, "2026-09-19T18:00")]
    [InlineData("2026-09-19T19:00", false, "2026-09-19T22:00")]
    [InlineData("2026-09-19T23:30", true, "2026-09-20T12:00")]
    public void AnOvernightTimetableIsReadInItsOwnTimezone(string now, bool blocked, string changesAt)
    {
        var windows = new List<DnsSiteWindow>
        {
            new("22:00", "12:00", []),
            new("15:00", "18:00", [])
        };

        var (isBlocked, changes) = DnsFilterPolicy.Evaluate(windows, Tallinn, Local(now));

        Assert.Equal(blocked, isBlocked);
        Assert.Equal(Local(changesAt), changes);
    }

    /// <summary>
    /// Two windows that meet are one stretch. Reported separately, a child would be told the
    /// sites come back at 18:00 when the next rule closes them again at the same minute.
    /// </summary>
    [Fact]
    public void WindowsThatMeetAreOneStretch()
    {
        var windows = new List<DnsSiteWindow>
        {
            new("15:00", "18:00", []),
            new("18:00", "20:00", [])
        };

        var (isBlocked, changes) = DnsFilterPolicy.Evaluate(windows, Tallinn, Local("2026-09-19T16:00"));

        Assert.True(isBlocked);
        Assert.Equal(Local("2026-09-19T20:00"), changes);
    }

    /// <summary>
    /// A window restricted to weekdays says nothing about Saturday, and still has to name the day
    /// it next applies - otherwise a Saturday reads as "blocked, forever".
    /// </summary>
    [Fact]
    public void ADayRestrictedWindowSkipsTheDaysItDoesNotCover()
    {
        var windows = new List<DnsSiteWindow>
        {
            new("09:00", "17:00", [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday])
        };

        // 2026-09-19 is a Saturday.
        var (isBlocked, changes) = DnsFilterPolicy.Evaluate(windows, Tallinn, Local("2026-09-19T12:00"));

        Assert.False(isBlocked);
        Assert.Equal(Local("2026-09-21T09:00"), changes);
    }

    [Fact]
    public void NoWindowsMeansNothingIsClosingAndNothingIsClosed()
    {
        var (isBlocked, changes) = DnsFilterPolicy.Evaluate([], Tallinn, Local("2026-09-19T12:00"));

        Assert.False(isBlocked);
        Assert.Null(changes);
    }

    /// <summary>
    /// The hour a spring clock change skips does not exist. A timetable written across it still
    /// has to produce an answer: a child told nothing is worse than a child told an hour late.
    /// </summary>
    [Fact]
    public void AClockChangeDoesNotStopTheTimetableBeingRead()
    {
        // Tallinn springs forward at 03:00 on the last Sunday of March; 03:30 never happens.
        var windows = new List<DnsSiteWindow> { new("03:30", "08:00", []) };

        var exception = Record.Exception(() =>
            DnsFilterPolicy.Evaluate(windows, Tallinn, Local("2026-03-29T10:00")));

        Assert.Null(exception);
    }

    // ------------------------------------------------ explaining a page that would not open

    /// <summary>
    /// The explanation is about the household's rules and never about the request, so it is the
    /// same answer whatever the child typed - and it carries the set of sites that is shut now,
    /// with the instant it comes back.
    /// </summary>
    [Fact]
    public void ARefusalIsExplainedFromTheCategoriesAndWhateverIsShutNow()
    {
        var now = Local("2026-09-19T20:00");
        var snapshot = new DnsFilteringSnapshot
        {
            State = DnsFilteringState.Active,
            Categories =
            [
                new DnsFilterCategory(DnsFilterCategoryKind.Malware, 1),
                new DnsFilterCategory(DnsFilterCategoryKind.Adult, 2)
            ],
            SiteGroups =
            [
                new DnsSiteGroup("Steam", 4, false, null, []),
                new DnsSiteGroup("Roblox", 12, true, Local("2026-09-20T09:00"), [])
            ]
        };

        var refusal = DnsFilterPolicy.ExplainRefusal(snapshot, now);

        Assert.NotNull(refusal);
        Assert.Equal(
            [DnsFilterCategoryKind.Malware, DnsFilterCategoryKind.Adult],
            refusal.Categories);
        Assert.Equal("Roblox", refusal.ClosedSiteGroup);
        Assert.Equal(Local("2026-09-20T09:00"), refusal.ReopensAtUtc);
    }

    /// <summary>
    /// Of several sets that are shut, the one coming back soonest is the one worth naming: it is
    /// the only one the child can do anything about, namely wait for it.
    /// </summary>
    [Fact]
    public void TheSetComingBackSoonestIsTheOneNamed()
    {
        var snapshot = new DnsFilteringSnapshot
        {
            State = DnsFilteringState.Active,
            SiteGroups =
            [
                new DnsSiteGroup("Always off", 2, true, null, []),
                new DnsSiteGroup("Late", 2, true, Local("2026-09-20T09:00"), []),
                new DnsSiteGroup("Soon", 2, true, Local("2026-09-19T21:00"), [])
            ]
        };

        var refusal = DnsFilterPolicy.ExplainRefusal(snapshot, Local("2026-09-19T20:00"));

        Assert.Equal("Soon", refusal?.ClosedSiteGroup);
    }

    /// <summary>
    /// A set with no timetable is shut until a parent says otherwise, so there is no time to give
    /// and the child is told the name without a promise attached to it.
    /// </summary>
    [Fact]
    public void ASetWithNoTimetableIsNamedWithoutATimeToComeBack()
    {
        var snapshot = new DnsFilteringSnapshot
        {
            State = DnsFilteringState.Active,
            SiteGroups = [new DnsSiteGroup("Roblox", 12, true, null, [])]
        };

        var refusal = DnsFilterPolicy.ExplainRefusal(snapshot, Local("2026-09-19T20:00"));

        Assert.Equal("Roblox", refusal?.ClosedSiteGroup);
        Assert.Null(refusal?.ReopensAtUtc);
    }

    /// <summary>
    /// Filtering that is off, absent, or never once read explains nothing, and saying so anyway
    /// would blame a rule that is not there for a page that failed for another reason entirely.
    /// </summary>
    [Theory]
    [InlineData(DnsFilteringState.NotConfigured)]
    [InlineData(DnsFilteringState.Unreachable)]
    [InlineData(DnsFilteringState.Inactive)]
    public void NothingIsExplainedWhenThereIsNoFilteringToExplain(DnsFilteringState state) => Assert.Null(
        DnsFilterPolicy.ExplainRefusal(
            new DnsFilteringSnapshot { State = state }, Local("2026-09-19T20:00")));

    [Fact]
    public void NothingIsExplainedWithoutASnapshotAtAll() =>
        Assert.Null(DnsFilterPolicy.ExplainRefusal(null, Local("2026-09-19T20:00")));

    /// <summary>
    /// Filtering that is on but blocks nothing at all has nothing to say either - an empty
    /// sentence on a child's screen is worse than no notification.
    /// </summary>
    [Fact]
    public void FilteringThatBlocksNothingExplainsNothing() => Assert.Null(
        DnsFilterPolicy.ExplainRefusal(
            new DnsFilteringSnapshot { State = DnsFilteringState.Active },
            Local("2026-09-19T20:00")));

    /// <summary>
    /// A snapshot read some time ago still describes the filtering that is in force, which is the
    /// same reason cached rules go on being enforced while a PC is offline.
    /// </summary>
    [Fact]
    public void AStaleSnapshotStillExplains()
    {
        var snapshot = new DnsFilteringSnapshot
        {
            State = DnsFilteringState.Active,
            IsStale = true,
            Categories = [new DnsFilterCategory(DnsFilterCategoryKind.Gambling, 1)]
        };

        Assert.NotNull(DnsFilterPolicy.ExplainRefusal(snapshot, Local("2026-09-19T20:00")));
    }
}
