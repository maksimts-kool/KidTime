using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

public sealed class LocalStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KidTime.Tests", Guid.NewGuid().ToString("N"));
    private string DatabaseFile => Path.Combine(_directory, "agent.db");

    [Fact]
    public async Task Cached_rules_survive_reopen_for_offline_enforcement()
    {
        Directory.CreateDirectory(_directory);
        var first = new LocalStore(DatabaseFile);
        await first.InitializeAsync(CancellationToken.None);
        await first.SaveRulesAsync(new DeviceRuleSnapshot
        {
            Revision = 42,
            TimeZoneId = "UTC",
            ControlledUserSid = "S-1-5-21-1000",
            ControlledUserName = "TESTPC\\child",
            ManuallyBlocked = true,
            DailyLimitSeconds = 300
        }, CancellationToken.None);

        var reopened = new LocalStore(DatabaseFile);
        await reopened.InitializeAsync(CancellationToken.None);
        var cached = await reopened.LoadRulesAsync(CancellationToken.None);

        Assert.NotNull(cached);
        Assert.Equal(42, cached.Revision);
        Assert.True(cached.ManuallyBlocked);
        Assert.Equal(300, cached.DailyLimitSeconds);
        Assert.Equal("S-1-5-21-1000", cached.ControlledUserSid);
        Assert.Equal("TESTPC\\child", cached.ControlledUserName);
    }

    [Fact]
    public async Task Pending_usage_is_durable_until_server_acknowledgement()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var date = new DateOnly(2026, 8, 23);
        await store.AddUsageAsync(date, null, 15, CancellationToken.None);
        await store.AddUsageAsync(date, "app", 15, CancellationToken.None);
        await store.PrepareUsageBatchAsync(CancellationToken.None);

        var reopened = new LocalStore(DatabaseFile);
        var pending = await reopened.GetPendingBatchesAsync(CancellationToken.None);

        var batch = Assert.Single(pending);
        Assert.Equal(2, batch.Deltas.Count);
        Assert.All(batch.Deltas, delta => Assert.Equal(15, delta.ActiveSeconds));
        await reopened.CompleteBatchAsync(batch.BatchId, CancellationToken.None);
        Assert.Empty(await reopened.GetPendingBatchesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Newly_observed_process_can_force_an_unchanged_application_to_resynchronize()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);

        await store.UpsertApplicationAsync(identity, app, CancellationToken.None);
        Assert.Single(await store.GetApplicationsToSyncAsync(CancellationToken.None));
        await store.MarkApplicationSynchronizedAsync(identity, CancellationToken.None);

        await store.UpsertApplicationAsync(identity, app, CancellationToken.None);
        Assert.Empty(await store.GetApplicationsToSyncAsync(CancellationToken.None));

        await store.UpsertApplicationAsync(identity, app, CancellationToken.None, forceSynchronization: true);
        Assert.Single(await store.GetApplicationsToSyncAsync(CancellationToken.None));
    }

    [Fact]
    public async Task First_application_block_grace_is_consumed_once_per_restriction_episode()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);

        Assert.True(await store.TryConsumeFirstApplicationBlockGraceAsync("firefox", "episode-1", CancellationToken.None));
        Assert.False(await store.TryConsumeFirstApplicationBlockGraceAsync("firefox", "episode-1", CancellationToken.None));
        Assert.True(await store.TryConsumeFirstApplicationBlockGraceAsync("firefox", "episode-2", CancellationToken.None));
        Assert.True(await store.TryConsumeFirstPcBlockGraceAsync("pc-episode-1", CancellationToken.None));
        Assert.False(await store.TryConsumeFirstPcBlockGraceAsync("pc-episode-1", CancellationToken.None));
    }

    [Fact]
    public async Task Idle_sample_is_excluded_and_active_foreground_sample_counts()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", IdleThresholdSeconds = 300 });
        var app = Descriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");

        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 5_000, 600, app), CancellationToken.None);
        Assert.Equal(0, await store.GetUsageAsync(date, null, CancellationToken.None));

        await coordinator.HandleSampleAsync(Sample(3, 10_000, 0, app), CancellationToken.None);
        Assert.Equal(5, await store.GetUsageAsync(date, null, CancellationToken.None));
        Assert.Equal(5, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(app), CancellationToken.None));
    }

    [Fact]
    public async Task Cached_application_limit_is_evaluated_without_server()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(date, identity, 60, CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 8,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 60 }]
        });

        var decision = await coordinator.EvaluateApplicationAsync(identity, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(BlockReason.DailyLimitReached, decision.Reason);
    }

    [Fact]
    public async Task Opening_a_time_limited_application_returns_an_availability_notification()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 9,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 3600 }]
        });

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);

        Assert.NotNull(state.Notification);
        Assert.Equal("Test app time available", state.Notification.Title);
        Assert.Contains("active time remains", state.Notification.Message);
    }

    [Fact]
    public async Task Pc_limit_change_queues_a_notification_regardless_of_foreground_application()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), NullLogger<EnforcementCoordinator>.Instance);
        var deviceId = Guid.NewGuid();
        coordinator.UpdateRules(new DeviceRuleSnapshot { DeviceId = deviceId, Revision = 1, TimeZoneId = "UTC", DailyLimitSeconds = 7200 });
        await coordinator.HandleSampleAsync(Sample(1, 0, 0, Descriptor()), CancellationToken.None);

        coordinator.UpdateRules(new DeviceRuleSnapshot { DeviceId = deviceId, Revision = 2, TimeZoneId = "UTC", DailyLimitSeconds = 3600 });
        var state = await coordinator.HandleSampleAsync(Sample(2, 1_000, 0, Descriptor()), CancellationToken.None);

        Assert.NotNull(state.Notification);
        Assert.Equal("PC time limit changed", state.Notification.Title);
        Assert.Contains("2h to 1h", state.Notification.Message);
    }

    [Fact]
    public async Task Open_application_limit_change_queues_a_notification()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);
        var deviceId = Guid.NewGuid();
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            DeviceId = deviceId,
            Revision = 1,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app" }]
        });
        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);

        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            DeviceId = deviceId,
            Revision = 2,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot
            {
                IdentityKey = identity,
                DisplayName = "Test app",
                DailyLimitSeconds = 3600,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }]
        });
        var state = await coordinator.HandleSampleAsync(Sample(2, 1_000, 0, app), CancellationToken.None);

        Assert.NotNull(state.Notification);
        Assert.Equal("Test app time limit changed", state.Notification.Title);
        Assert.Contains("no limit to 1h", state.Notification.Message);
    }

    [Fact]
    public async Task Native_message_focus_bounce_does_not_repeat_the_application_notification()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 9,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 3600 }]
        });

        var opened = await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);
        var messageTookFocus = await coordinator.HandleSampleAsync(
            new SessionUsageSample(2, 1_000, false, 0, Environment.ProcessId, "Windows", null),
            CancellationToken.None);
        var returnedToApp = await coordinator.HandleSampleAsync(Sample(3, 2_000, 0, app), CancellationToken.None);

        Assert.NotNull(opened.Notification);
        Assert.Null(messageTookFocus.Notification);
        Assert.Null(returnedToApp.Notification);
    }

    [Fact]
    public async Task Application_warning_is_queued_when_fifteen_minutes_remain()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var app = Descriptor();
        var identity = ApplicationIdentity.CreateKey(app);
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(date, identity, 99, CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 10,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 1000 }]
        });
        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);

        var state = await coordinator.HandleSampleAsync(Sample(2, 2_000, 0, app), CancellationToken.None);

        Assert.NotNull(state.Notification);
        Assert.Equal("Test app time warning", state.Notification.Title);
        Assert.Contains("15 minutes remaining", state.Notification.Message);
        Assert.Null(state.Notification.CountdownSeconds);

        await store.AddUsageAsync(date, identity, 600, CancellationToken.None);
        state = await coordinator.HandleSampleAsync(Sample(3, 2_000, 0, app), CancellationToken.None);
        Assert.NotNull(state.Notification);
        Assert.Contains("5 minutes remaining", state.Notification.Message);
        Assert.Null(state.Notification.CountdownSeconds);

        await store.AddUsageAsync(date, identity, 180, CancellationToken.None);
        state = await coordinator.HandleSampleAsync(Sample(4, 2_000, 0, app), CancellationToken.None);
        Assert.NotNull(state.Notification);
        Assert.Contains("2 minutes remaining", state.Notification.Message);
        Assert.Null(state.Notification.CountdownSeconds);
    }

    [Fact]
    public async Task Pc_warning_is_queued_when_fifteen_minutes_remain()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var app = Descriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(date, null, 99, CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 11, TimeZoneId = "UTC", DailyLimitSeconds = 1000 });
        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);

        var state = await coordinator.HandleSampleAsync(Sample(2, 2_000, 0, app), CancellationToken.None);

        Assert.NotNull(state.Notification);
        Assert.Equal("PC time warning", state.Notification.Title);
        Assert.Contains("sign you out", state.Notification.Message);
        Assert.Null(state.Notification.CountdownSeconds);
    }

    [Fact]
    public async Task Only_final_enforcement_warnings_have_a_persistent_countdown()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 12, TimeZoneId = "UTC" });
        coordinator.NotifyApplicationClosing(
            "test-app",
            "Test app",
            new RuleDecision(false, BlockReason.ManualBlock, "Test app is blocked."),
            60);

        var appWarning = await coordinator.HandleSampleAsync(
            new SessionUsageSample(1, 0, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        Assert.NotNull(appWarning.Notification);
        Assert.Equal(60, appWarning.Notification.CountdownSeconds);
        Assert.Equal("application:test-app", appWarning.Notification.PersistentNotificationKey);
        Assert.False(appWarning.Notification.DismissPersistentNotification);

        coordinator.DismissApplicationClosing("test-app");
        var dismissal = await coordinator.HandleSampleAsync(
            new SessionUsageSample(2, 1_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        Assert.NotNull(dismissal.Notification);
        Assert.True(dismissal.Notification.DismissPersistentNotification);
        Assert.Equal("application:test-app", dismissal.Notification.PersistentNotificationKey);

        coordinator.NotifyPcSignOut("Windows will sign you out in 60 seconds.", 60);
        var pcWarning = await coordinator.HandleSampleAsync(
            new SessionUsageSample(3, 2_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        Assert.NotNull(pcWarning.Notification);
        Assert.Equal(60, pcWarning.Notification.CountdownSeconds);
        Assert.Equal("pc-sign-out", pcWarning.Notification.PersistentNotificationKey);

        coordinator.DismissPcSignOut();
        var pcDismissal = await coordinator.HandleSampleAsync(
            new SessionUsageSample(4, 3_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        Assert.NotNull(pcDismissal.Notification);
        Assert.True(pcDismissal.Notification.DismissPersistentNotification);
        Assert.Equal("pc-sign-out", pcDismissal.Notification.PersistentNotificationKey);
    }

    [Fact]
    public async Task User_status_separates_daily_schedule_app_and_connection_information()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        await store.AddUsageAsync(date, null, 600, CancellationToken.None);
        await store.AddUsageAsync(date, "limited-app", 120, CancellationToken.None);

        var schedule = new WeeklySchedule
        {
            Days =
            [
                new DaySchedule
                {
                    Day = DayOfWeek.Monday,
                    Windows = [new TimeWindow(new TimeOnly(11, 0), new TimeOnly(14, 0))]
                }
            ]
        };
        var coordinator = new EnforcementCoordinator(store, clock, NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 19,
            TimeZoneId = "UTC",
            ControlledUserName = "TESTPC\\child",
            DailyLimitSeconds = 3_600,
            Schedule = schedule,
            Applications =
            [
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "limited-app",
                    DisplayName = "Limited app",
                    DailyLimitSeconds = 1_200,
                    Schedule = schedule
                },
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "blocked-app",
                    DisplayName = "Blocked app",
                    ManuallyBlocked = true
                },
                new ApplicationRuleSnapshot
                {
                    IdentityKey = "unrestricted-app",
                    DisplayName = "Unrestricted app"
                }
            ]
        });
        var server = new ServerConnectionStatus(
            true,
            "Connected",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);

        var status = await coordinator.GetUserStatusAsync(server, CancellationToken.None);

        Assert.Equal("TESTPC\\child", status.ControlledUserName);
        Assert.Equal(19, status.RuleRevision);
        Assert.Equal(3_000, status.ScreenTime.DailyRemainingSeconds);
        Assert.True(status.ScreenTime.IsWithinSchedule);
        Assert.NotNull(status.ScreenTime.ScheduleAvailableUntilUtc);
        Assert.Equal(2, status.Applications.Count);
        var limited = Assert.Single(status.Applications, item => item.IdentityKey == "limited-app");
        Assert.Equal(1_080, limited.Allowance.DailyRemainingSeconds);
        Assert.True(limited.Allowance.IsAllowed);
        var blocked = Assert.Single(status.Applications, item => item.IdentityKey == "blocked-app");
        Assert.True(blocked.IsManuallyBlocked);
        Assert.False(blocked.Allowance.IsAllowed);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static ApplicationDescriptor Descriptor() => new()
    {
        DisplayName = "Test app",
        ExecutableName = "test.exe",
        ExecutablePath = @"C:\Apps\test.exe",
        ProductName = "Test app",
        OriginalFilename = "test.exe",
        SignaturePublisher = "Test Publisher"
    };

    private static SessionUsageSample Sample(long sequence, long elapsedMs, int idleSeconds, ApplicationDescriptor app) =>
        new(sequence, elapsedMs, idleSeconds >= 300, idleSeconds, Environment.ProcessId, "Test", app);
}
