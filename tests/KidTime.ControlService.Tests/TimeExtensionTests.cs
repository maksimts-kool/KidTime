using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
using KidTime.Domain.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

/// <summary>
/// The controlled PC's half of asking for more time. The point of every test here is that the
/// request is a question and nothing else: it is refused when asking makes no sense, it is
/// durable once accepted, and the minutes only ever arrive from the parent.
/// </summary>
public sealed class TimeExtensionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KidTime.Tests", Guid.NewGuid().ToString("N"));
    private string DatabaseFile => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task Extra_time_cannot_be_asked_for_while_plenty_is_left()
    {
        var (_, extensions) = await NewServiceAsync();

        var result = await extensions.SubmitAsync(
            Today, null, "PC", 30, remainingSeconds: 3600, Period, AgentStrings.English, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(AgentStrings.English.ExtraTimeNotRunningOutYet, result.Message);
    }

    [Fact]
    public async Task An_unlimited_allowance_cannot_be_extended()
    {
        var (_, extensions) = await NewServiceAsync();

        var result = await extensions.SubmitAsync(
            Today, null, "PC", 30, remainingSeconds: null, Period, AgentStrings.English, CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(AgentStrings.English.ExtraTimeNotPossible, result.Message);
    }

    [Fact]
    public async Task An_amount_outside_the_offered_ones_is_refused()
    {
        var (_, extensions) = await NewServiceAsync();

        var result = await extensions.SubmitAsync(
            Today, null, "PC", 240, remainingSeconds: 60, Period, AgentStrings.English, CancellationToken.None);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task A_second_request_is_refused_while_the_first_is_unanswered()
    {
        var (_, extensions) = await NewServiceAsync();

        var first = await extensions.SubmitAsync(
            Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);
        var second = await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, Period, AgentStrings.English, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(AgentStrings.English.ExtraTimeAlreadyAsked, second.Message);
    }

    [Fact]
    public async Task A_pending_request_for_the_PC_does_not_block_asking_about_an_application()
    {
        var (_, extensions) = await NewServiceAsync();

        var pc = await extensions.SubmitAsync(Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);
        var app = await extensions.SubmitAsync(
            Today, "roblox", "Roblox", 15, 60, Period, AgentStrings.English, CancellationToken.None);

        Assert.True(pc.Accepted);
        Assert.True(app.Accepted);
    }

    [Fact]
    public async Task Asking_stops_at_the_daily_cap()
    {
        var (_, extensions) = await NewServiceAsync();

        // Each round is answered, and each is a fresh allowance period, so neither the pending
        // request nor the refusal is what stops it - the cap has to.
        for (var round = 0; round < TimeExtensionPolicy.MaximumRequestsPerDay; round++)
        {
            var accepted = await extensions.SubmitAsync(
                Today, null, "PC", 15, 60, $"window:{round}", AgentStrings.English, CancellationToken.None);
            Assert.True(accepted.Accepted);
            await AnswerEverythingAsync(extensions, TimeExtensionStatus.Denied);
        }

        var refused = await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:last", AgentStrings.English, CancellationToken.None);

        Assert.False(refused.Accepted);
        Assert.Equal(AgentStrings.English.ExtraTimeTooManyToday, refused.Message);
    }

    [Fact]
    public async Task A_refusal_holds_until_the_next_allowance_period()
    {
        var (_, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:afternoon", AgentStrings.English, CancellationToken.None);
        await AnswerEverythingAsync(extensions, TimeExtensionStatus.Denied);

        var sameWindow = await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:afternoon", AgentStrings.English, CancellationToken.None);
        var sameWindowState = await extensions.GetStateAsync(
            Today, null, "window:afternoon", CancellationToken.None);
        // The schedule closed and opened again; this is a new stretch of screen time.
        var nextWindow = await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:evening", AgentStrings.English, CancellationToken.None);

        Assert.False(sameWindow.Accepted);
        Assert.Equal(AgentStrings.English.ExtraTimeDeniedUntilNextPeriod, sameWindow.Message);
        Assert.Equal(TimeExtensionOfferState.Denied, sameWindowState.State);
        Assert.True(nextWindow.Accepted);
    }

    [Fact]
    public async Task A_refusal_from_an_earlier_period_leaves_the_buttons_back()
    {
        var (_, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:afternoon", AgentStrings.English, CancellationToken.None);
        await AnswerEverythingAsync(extensions, TimeExtensionStatus.Denied);

        var duringRefusal = await extensions.GetStateAsync(
            Today, null, "window:afternoon", CancellationToken.None);
        var afterRefusal = await extensions.GetStateAsync(
            Today, null, "window:evening", CancellationToken.None);

        Assert.Equal(TimeExtensionOfferState.Denied, duringRefusal.State);
        // Not a caption about something that is over - the child simply may ask again.
        Assert.Equal(TimeExtensionOfferState.Available, afterRefusal.State);
    }

    [Fact]
    public async Task A_refusal_for_the_PC_does_not_lock_an_application()
    {
        var (_, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(
            Today, null, "PC", 15, 60, "window:afternoon", AgentStrings.English, CancellationToken.None);
        await AnswerEverythingAsync(extensions, TimeExtensionStatus.Denied);

        var application = await extensions.SubmitAsync(
            Today, "roblox", "Roblox", 15, 60, "window:afternoon", AgentStrings.English, CancellationToken.None);

        Assert.True(application.Accepted);
    }

    [Fact]
    public async Task A_request_survives_a_restart_before_it_is_uploaded()
    {
        var (store, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);

        var reopened = new LocalStore(DatabaseFile);
        await reopened.InitializeAsync(CancellationToken.None);
        var pending = await reopened.GetTimeExtensionsToUploadAsync(CancellationToken.None);

        Assert.NotSame(store, reopened);
        var request = Assert.Single(pending);
        Assert.Equal(30, request.RequestedMinutes);
        Assert.Equal(TimeExtensionStatus.Pending, request.Status);
        Assert.Null(request.ApplicationIdentityKey);
    }

    [Fact]
    public async Task An_uploaded_request_is_not_sent_again()
    {
        var (_, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);

        var first = await extensions.GetPendingUploadsAsync(CancellationToken.None);
        await extensions.MarkUploadedAsync(first[0].RequestId, CancellationToken.None);
        var second = await extensions.GetPendingUploadsAsync(CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public async Task An_answer_is_announced_once_and_a_repeated_decision_changes_nothing()
    {
        var (_, extensions) = await NewServiceAsync();
        await extensions.SubmitAsync(Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);
        var request = (await extensions.GetPendingUploadsAsync(CancellationToken.None))[0];
        var decision = new TimeExtensionDecision(
            request.RequestId, TimeExtensionStatus.Approved, 20, "PC", DateTimeOffset.UtcNow);

        await extensions.ApplyDecisionsAsync([decision], CancellationToken.None);
        var announcements = await extensions.GetAnnouncementsAsync(CancellationToken.None);
        foreach (var item in announcements) await extensions.MarkAnnouncedAsync(item.RequestId, CancellationToken.None);

        // The server keeps repeating recent decisions; that must not tell the child twice, and
        // must not overwrite the granted amount with a different one.
        await extensions.ApplyDecisionsAsync(
            [decision with { GrantedMinutes = 99 }], CancellationToken.None);
        var repeated = await extensions.GetAnnouncementsAsync(CancellationToken.None);

        var announced = Assert.Single(announcements);
        Assert.Equal(TimeExtensionStatus.Approved, announced.Status);
        Assert.Equal(20, announced.GrantedMinutes);
        Assert.Empty(repeated);
    }

    [Fact]
    public async Task The_card_state_follows_the_last_answer_for_that_scope()
    {
        var (_, extensions) = await NewServiceAsync();

        var before = await extensions.GetStateAsync(Today, null, Period, CancellationToken.None);
        await extensions.SubmitAsync(Today, null, "PC", 30, 60, Period, AgentStrings.English, CancellationToken.None);
        var pending = await extensions.GetStateAsync(Today, null, Period, CancellationToken.None);
        var request = (await extensions.GetPendingUploadsAsync(CancellationToken.None))[0];
        await extensions.ApplyDecisionsAsync(
            [new TimeExtensionDecision(request.RequestId, TimeExtensionStatus.Approved, 20, "PC", DateTimeOffset.UtcNow)],
            CancellationToken.None);
        var granted = await extensions.GetStateAsync(Today, null, Period, CancellationToken.None);

        Assert.Equal(TimeExtensionOfferState.Available, before.State);
        Assert.Equal(TimeExtensionOfferState.Pending, pending.State);
        // A granted request is a caption, not a lock: once those minutes are also spent the child
        // may ask again, and the daily cap is what stops it.
        Assert.Equal(TimeExtensionOfferState.Granted, granted.State);
        Assert.Equal(20, granted.Minutes);
    }

    [Fact]
    public async Task Extra_time_is_offered_only_once_an_allowance_is_nearly_spent()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(
            store, clock, Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            DailyLimitSeconds = 600
        });

        var early = await AdvanceAsync(coordinator, 120);
        // Past the point where less than five minutes is left.
        var late = await AdvanceAsync(coordinator, 200);

        Assert.Empty(early.ExtensionOffers ?? []);
        var offer = Assert.Single(late.ExtensionOffers ?? []);
        Assert.Null(offer.ApplicationIdentityKey);
        Assert.Equal(TimeExtensionOfferState.Available, offer.State);
    }

    [Fact]
    public async Task Extra_time_is_still_offered_after_the_limit_has_run_out()
    {
        // The moment a child most wants to ask is the moment the sign-out card appeared, and by
        // then the PC is already blocked. An offer that disappeared there would be useless.
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(
            store, new TrustedClock(), Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            DailyLimitSeconds = 60
        });

        var exhausted = await AdvanceAsync(coordinator, 90);

        Assert.True(exhausted.IsPcBlocked);
        var offer = Assert.Single(exhausted.ExtensionOffers ?? []);
        Assert.Equal(0, offer.RemainingSeconds);
    }

    [Fact]
    public async Task A_manual_block_is_not_something_extra_time_can_be_asked_about()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(
            store, new TrustedClock(), Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            DailyLimitSeconds = 60,
            ManuallyBlocked = true
        });

        var blocked = await AdvanceAsync(coordinator, 90);

        Assert.True(blocked.IsPcBlocked);
        Assert.Empty(blocked.ExtensionOffers ?? []);
    }

    [Fact]
    public async Task Granted_extra_time_reaches_enforcement_through_the_rule_snapshot()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(
            store, clock, Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        var rules = new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            DailyLimitSeconds = 60
        };
        coordinator.UpdateRules(rules);
        var blocked = await AdvanceAsync(coordinator, 90);

        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 2,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            DailyLimitSeconds = 60,
            Bonus = new TimeBonus(RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC"), 900)
        });
        var extended = await AdvanceAsync(coordinator, 2);

        Assert.True(blocked.IsPcBlocked);
        Assert.False(extended.IsPcBlocked);
        Assert.Equal(960, extended.DailyLimitSeconds);
    }

    [Fact]
    public async Task Every_application_that_is_nearly_out_of_time_is_offered_it_on_its_own_card()
    {
        // The Apps tab is where a child looks for the application that is running out, so the
        // offer cannot be limited to whatever happens to be in the foreground.
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var today = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(today, "nearly-spent", 1_150, CancellationToken.None);
        await store.AddUsageAsync(today, "plenty-left", 60, CancellationToken.None);
        var coordinator = new EnforcementCoordinator(
            store, clock, Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            Applications =
            [
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "nearly-spent",
                    DisplayName = "Roblox",
                    DailyLimitSeconds = 1_200
                },
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "plenty-left",
                    DisplayName = "Minecraft",
                    DailyLimitSeconds = 3_600
                },
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "blocked-outright",
                    DisplayName = "Steam",
                    ManuallyBlocked = true
                }
            ]
        });

        var status = await coordinator.GetUserStatusAsync(Server, CancellationToken.None);

        var nearlySpent = Assert.Single(status.Applications, item => item.IdentityKey == "nearly-spent");
        Assert.NotNull(nearlySpent.Extension);
        Assert.Equal("Roblox", nearlySpent.Extension.DisplayName);
        Assert.Equal(TimeExtensionOfferState.Available, nearlySpent.Extension.State);
        // Plenty of time left, so there is nothing to ask about yet.
        Assert.Null(Assert.Single(status.Applications, item => item.IdentityKey == "plenty-left").Extension);
        // Extra time raises a limit; it cannot lift a block, so offering it would be a lie.
        Assert.Null(Assert.Single(status.Applications, item => item.IdentityKey == "blocked-outright").Extension);
    }

    [Fact]
    public async Task An_application_card_stops_offering_while_its_request_is_unanswered()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var today = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(today, "nearly-spent", 1_150, CancellationToken.None);
        var coordinator = new EnforcementCoordinator(
            store, clock, Extensions(store), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            Applications =
            [
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "nearly-spent",
                    DisplayName = "Roblox",
                    DailyLimitSeconds = 1_200
                }
            ]
        });

        var accepted = await coordinator.RequestTimeExtensionAsync(
            new TimeExtensionSubmission(15, "nearly-spent"), CancellationToken.None);
        var status = await coordinator.GetUserStatusAsync(Server, CancellationToken.None);

        Assert.True(accepted.Accepted);
        var offer = Assert.Single(status.Applications).Extension;
        Assert.NotNull(offer);
        Assert.Equal(TimeExtensionOfferState.Pending, offer.State);
    }

    private static ServerConnectionStatus Server => new(
        true,
        ServerConnectionState.Connected,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>One stretch of screen time, for the tests that only ever describe one.</summary>
    private const string Period = "window:test";

    private async Task<(LocalStore Store, TimeExtensionService Extensions)> NewServiceAsync()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        return (store, Extensions(store));
    }

    private static TimeExtensionService Extensions(LocalStore store) =>
        new(store, NullLogger<TimeExtensionService>.Instance);

    private static async Task AnswerEverythingAsync(TimeExtensionService extensions, TimeExtensionStatus status)
    {
        foreach (var request in await extensions.GetPendingUploadsAsync(CancellationToken.None))
        {
            await extensions.ApplyDecisionsAsync(
                [new TimeExtensionDecision(request.RequestId, status, 0, request.DisplayName, DateTimeOffset.UtcNow)],
                CancellationToken.None);
            await extensions.MarkUploadedAsync(request.RequestId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Plays active foreground samples forward. A single sample's delta is capped at 30 seconds
    /// so a suspend cannot create a jump, so time is advanced in steps rather than in one leap.
    /// </summary>
    private async Task<EnforcementState> AdvanceAsync(EnforcementCoordinator coordinator, int seconds)
    {
        var state = await coordinator.HandleSampleAsync(Sample(++_sequence, _elapsedMs), CancellationToken.None);
        var remaining = seconds;
        while (remaining > 0)
        {
            var step = Math.Min(30, remaining);
            remaining -= step;
            _elapsedMs += step * 1_000;
            state = await coordinator.HandleSampleAsync(Sample(++_sequence, _elapsedMs), CancellationToken.None);
        }

        return state;
    }

    private long _sequence;
    private long _elapsedMs;

    private static SessionUsageSample Sample(long sequence, long elapsedMs) =>
        new(sequence, elapsedMs, false, 0, Environment.ProcessId, "Test", new ApplicationDescriptor
        {
            DisplayName = "Test app",
            ExecutableName = "test.exe",
            ExecutablePath = @"C:\Apps\test.exe",
            ProductName = "Test app",
            OriginalFilename = "test.exe",
            SignaturePublisher = "Test Publisher"
        });
}
