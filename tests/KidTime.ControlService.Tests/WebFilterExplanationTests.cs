using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Server;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

/// <summary>
/// Telling a child why a page would not open. The value of the feature is entirely in when it
/// stays quiet: an explanation that arrives every two seconds, or that blames the household's
/// filter for a network outage, teaches the child to ignore it.
/// </summary>
public sealed class WebFilterExplanationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "KidTime.Tests", Guid.NewGuid().ToString("N"));

    private string DatabaseFile => Path.Combine(_directory, "kidtime.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task A_page_that_fails_is_explained_from_the_filtering_that_is_in_force()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.NameNotResolved), CancellationToken.None);

        var notification = Assert.Single(state.Notifications);
        Assert.Contains("Roblox", notification.Message, StringComparison.Ordinal);
        Assert.Contains("09:00", notification.Message, StringComparison.Ordinal);
        // Nothing is closing, so nothing interrupts: an ordinary toast with no countdown.
        Assert.False(notification.IsUrgent);
        Assert.Null(notification.CountdownSeconds);
    }

    /// <summary>
    /// The child arriving on an error page is the news. Sitting there reading it is not, and a
    /// browser retrying in the background must not be able to turn one explanation into a stream.
    /// </summary>
    [Fact]
    public async Task Sitting_on_the_error_page_is_explained_once()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        var first = await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.NameNotResolved), CancellationToken.None);
        var second = await coordinator.HandleSampleAsync(Sample(2, 2_000, BrowserPageError.NameNotResolved), CancellationToken.None);
        var third = await coordinator.HandleSampleAsync(Sample(3, 4_000, BrowserPageError.NameNotResolved), CancellationToken.None);

        Assert.Single(first.Notifications);
        Assert.Empty(second.Notifications);
        Assert.Empty(third.Notifications);
    }

    /// <summary>
    /// Leaving the error page and coming back is a second attempt, but within the quiet period it
    /// is the same question - the child has already been told the answer.
    /// </summary>
    [Fact]
    public async Task A_second_attempt_soon_afterwards_is_not_explained_again()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.NameNotResolved), CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 2_000, BrowserPageError.None), CancellationToken.None);
        var again = await coordinator.HandleSampleAsync(Sample(3, 4_000, BrowserPageError.NameNotResolved), CancellationToken.None);

        Assert.Empty(again.Notifications);
    }

    /// <summary>
    /// A home network that is down produces exactly the same error page, and the service can tell
    /// the difference because it cannot reach the server either. Blaming the filter there would be
    /// a confident lie the child has no way to check.
    /// </summary>
    [Fact]
    public async Task Nothing_is_explained_while_the_PC_cannot_reach_the_server()
    {
        var coordinator = await CoordinatorAsync(new AgentRuntimeStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.NameNotResolved), CancellationToken.None);

        Assert.Empty(state.Notifications);
    }

    [Fact]
    public async Task Nothing_is_explained_where_no_filtering_is_configured()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.NameNotResolved), CancellationToken.None);

        Assert.Empty(state.Notifications);
    }

    /// <summary>
    /// The shape a household's DNS server usually produces: blocked names are answered with an
    /// address of its own, the block page has no certificate for the site that was asked for, and
    /// the child is stopped by a security warning rather than by a missing name. This is the case
    /// the feature missed on the first real deployment.
    /// </summary>
    [Fact]
    public async Task A_block_page_with_the_wrong_certificate_is_explained()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        var state = await coordinator.HandleSampleAsync(
            Sample(1, 0, BrowserPageError.SecureConnectionFailed), CancellationToken.None);

        var notification = Assert.Single(state.Notifications);
        Assert.Contains("Roblox", notification.Message, StringComparison.Ordinal);
        // The child is looking at a security warning, and the same warning appears on a site that
        // is genuinely unsafe. Telling them to check the address they typed would read as
        // permission to click through it.
        Assert.DoesNotContain("check it", notification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(notification.IsUrgent);
    }

    [Fact]
    public async Task An_ordinary_page_says_nothing()
    {
        var coordinator = await CoordinatorAsync(ConnectedStatus());
        coordinator.UpdateDnsFiltering(Filtering());

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, BrowserPageError.None), CancellationToken.None);

        Assert.Empty(state.Notifications);
    }

    private async Task<EnforcementCoordinator> CoordinatorAsync(AgentRuntimeStatus status)
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(
            store,
            new TrustedClock(),
            new TimeExtensionService(store, NullLogger<TimeExtensionService>.Instance),
            status,
            NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", IdleThresholdSeconds = 300 });
        return coordinator;
    }

    private static AgentRuntimeStatus ConnectedStatus()
    {
        var status = new AgentRuntimeStatus();
        status.MarkSynchronizationSucceeded();
        return status;
    }

    /// <summary>Adult sites blocked at every hour, and Roblox shut until nine in the morning.</summary>
    private static DnsFilteringSnapshot Filtering() => new()
    {
        State = DnsFilteringState.Active,
        RetrievedAtUtc = DateTimeOffset.UtcNow,
        Categories = [new DnsFilterCategory(DnsFilterCategoryKind.Adult, 1)],
        SiteGroups =
        [
            // Nine tomorrow, said in UTC because the rules under test are: DateTimeOffset.Date
            // hands back an unspecified DateTime, which converts at the machine's own offset, and
            // the assertion on "09:00" then only held on a build agent that happened to be on UTC.
            new DnsSiteGroup("Roblox", 12, true, new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(9), TimeSpan.Zero), [])
        ]
    };

    private static SessionUsageSample Sample(long sequence, long elapsedMs, BrowserPageError page) =>
        new(sequence, elapsedMs, false, 0, Environment.ProcessId, "Test", Firefox(), BrowserPage: page);

    private static ApplicationDescriptor Firefox() => new()
    {
        DisplayName = "Firefox",
        ExecutableName = "firefox.exe",
        ExecutablePath = @"C:\Program Files\Mozilla Firefox\firefox.exe",
        ProductName = "Firefox",
        OriginalFilename = "firefox.exe",
        SignaturePublisher = "Mozilla Corporation"
    };
}
