using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Server;
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", IdleThresholdSeconds = 300 });
        var app = Descriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");

        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 5_000, 600, app), CancellationToken.None);
        await coordinator.FlushUsageAsync(CancellationToken.None);
        Assert.Equal(0, await store.GetUsageAsync(date, null, CancellationToken.None));

        await coordinator.HandleSampleAsync(Sample(3, 10_000, 0, app), CancellationToken.None);
        await coordinator.FlushUsageAsync(CancellationToken.None);
        Assert.Equal(5, await store.GetUsageAsync(date, null, CancellationToken.None));
        Assert.Equal(5, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(app), CancellationToken.None));
    }

    [Fact]
    public async Task An_application_in_a_call_counts_from_the_background_even_while_the_keyboard_is_idle()
    {
        // A child in a Discord call behind a game is using Discord the whole time. Holding the
        // microphone is the call itself, so it counts without input; PC time keeps its idle rule.
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", IdleThresholdSeconds = 300 });
        var game = Descriptor();
        var call = CallDescriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        AudibleApplication[] inCall = [new(call, IsCapturing: true)];

        await coordinator.HandleSampleAsync(Sample(1, 0, 0, game) with { AudibleApplications = inCall }, CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 5_000, 0, game) with { AudibleApplications = inCall }, CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(3, 10_000, 600, game) with { AudibleApplications = inCall }, CancellationToken.None);
        await coordinator.FlushUsageAsync(CancellationToken.None);

        Assert.Equal(5, await store.GetUsageAsync(date, null, CancellationToken.None));
        Assert.Equal(5, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(game), CancellationToken.None));
        Assert.Equal(10, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(call), CancellationToken.None));
    }

    [Fact]
    public async Task Sound_alone_counts_only_while_somebody_is_at_the_pc_and_never_twice()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", IdleThresholdSeconds = 300 });
        var game = Descriptor();
        var music = CallDescriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        AudibleApplication[] playing =
        [
            new(music, IsCapturing: false),
            // The foreground application playing its own sound is still one application.
            new(game, IsCapturing: false),
            // Anything the catalog would refuse from the foreground is refused here too.
            new(new ApplicationDescriptor
            {
                DisplayName = "Microsoft Edge WebView2",
                ExecutableName = "msedgewebview2.exe",
                ExecutablePath = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\msedgewebview2.exe"
            }, IsCapturing: true)
        ];

        await coordinator.HandleSampleAsync(Sample(1, 0, 0, game) with { AudibleApplications = playing }, CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 5_000, 0, game) with { AudibleApplications = playing }, CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(3, 10_000, 600, game) with { AudibleApplications = playing }, CancellationToken.None);
        await coordinator.FlushUsageAsync(CancellationToken.None);

        Assert.Equal(5, await store.GetUsageAsync(date, null, CancellationToken.None));
        Assert.Equal(5, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(game), CancellationToken.None));
        Assert.Equal(5, await store.GetUsageAsync(date, ApplicationIdentity.CreateKey(music), CancellationToken.None));
    }

    [Fact]
    public async Task Counted_seconds_are_buffered_between_flushes_and_survive_a_restart()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 1, TimeZoneId = "UTC", DailyLimitSeconds = 3_600 });
        var app = Descriptor();
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");

        await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);
        var state = await coordinator.HandleSampleAsync(Sample(2, 20_000, 0, app), CancellationToken.None);

        // Enforcement sees the seconds immediately even though the database has not been touched.
        Assert.Equal(20, state.TodayActiveSeconds);
        Assert.Equal(0, await store.GetUsageAsync(date, null, CancellationToken.None));

        await coordinator.FlushUsageAsync(CancellationToken.None);
        Assert.Equal(20, await store.GetUsageAsync(date, null, CancellationToken.None));

        var reopened = new LocalStore(DatabaseFile);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal(20, await reopened.GetUsageAsync(date, null, CancellationToken.None));
    }

    [Fact]
    public async Task Diagnostic_reports_are_queued_until_the_server_accepts_them()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var report = new DiagnosticReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DiagnosticComponents.SessionAgent,
            DiagnosticSeverities.Fatal,
            "The KidTime tray agent stopped because of an unhandled error.",
            "System.InvalidOperationException",
            "at KidTime.SessionAgent.AgentApplicationHost..ctor()",
            "0.2.21");
        await store.QueueDiagnosticAsync(report, DiagnosticReportPolicy.CreateFingerprint(report), CancellationToken.None);

        var reopened = new LocalStore(DatabaseFile);
        await reopened.InitializeAsync(CancellationToken.None);
        var pending = await reopened.GetPendingDiagnosticsAsync(CancellationToken.None);

        var queued = Assert.Single(pending);
        Assert.Equal(report.ReportId, queued.ReportId);
        Assert.Equal(DiagnosticSeverities.Fatal, queued.Severity);

        await reopened.CompleteDiagnosticsAsync([queued.ReportId], CancellationToken.None);
        Assert.Empty(await reopened.GetPendingDiagnosticsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reporting_a_fault_spools_it_and_flushing_moves_it_into_the_upload_queue()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var reporter = new DiagnosticReporter(Path.Combine(_directory, "diagnostics.ndjson"));

        reporter.ReportFatal("The control service stopped because of an unhandled exception.", new InvalidOperationException("boom"));
        // A crash loop reports the same fault repeatedly; the queue keeps one entry for it.
        reporter.ReportFatal("The control service stopped because of an unhandled exception.", new InvalidOperationException("boom"));
        await reporter.FlushToStoreAsync(store, CancellationToken.None);

        var pending = await store.GetPendingDiagnosticsAsync(CancellationToken.None);
        var queued = Assert.Single(pending);
        Assert.Equal(DiagnosticComponents.ControlService, queued.Component);
        Assert.Equal("System.InvalidOperationException", queued.ExceptionType);
        Assert.Contains("boom", queued.Detail);
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 9,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 3600 }]
        });

        var state = await coordinator.HandleSampleAsync(Sample(1, 0, 0, app), CancellationToken.None);

        var notification = Assert.Single(state.Notifications);
        Assert.Equal("Test app time", notification.Title);
        Assert.Contains("left today", notification.Message);
        Assert.False(notification.IsUrgent);
    }

    [Fact]
    public async Task Pc_limit_change_queues_a_notification_regardless_of_foreground_application()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        var deviceId = Guid.NewGuid();
        coordinator.UpdateRules(new DeviceRuleSnapshot { DeviceId = deviceId, Revision = 1, TimeZoneId = "UTC", DailyLimitSeconds = 7200 });
        await coordinator.HandleSampleAsync(Sample(1, 0, 0, Descriptor()), CancellationToken.None);

        coordinator.UpdateRules(new DeviceRuleSnapshot { DeviceId = deviceId, Revision = 2, TimeZoneId = "UTC", DailyLimitSeconds = 3600 });
        var state = await coordinator.HandleSampleAsync(Sample(2, 1_000, 0, Descriptor()), CancellationToken.None);

        var notification = Assert.Single(state.Notifications);
        Assert.Equal("PC time limit changed", notification.Title);
        Assert.Contains("daily time is now 1h", notification.Message);
        Assert.False(notification.IsUrgent);
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
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
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

        var notification = Assert.Single(state.Notifications);
        Assert.Equal("Test app time limit changed", notification.Title);
        Assert.Contains("daily time is now 1h", notification.Message);
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
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

        Assert.Single(opened.Notifications);
        Assert.Empty(messageTookFocus.Notifications);
        Assert.Empty(returnedToApp.Notifications);
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot
        {
            Revision = 10,
            TimeZoneId = "UTC",
            Applications = [new ApplicationRuleSnapshot { IdentityKey = identity, DisplayName = "Test app", DailyLimitSeconds = 1000 }]
        });
        var notifications = await AccrueAsync(coordinator, app, 800);

        var warnings = notifications.Where(item => item.Title.Contains("of Test app left")).ToList();
        var titles = warnings.Select(item => item.Title).ToList();
        Assert.Contains("15 minutes of Test app left", titles);
        Assert.Contains("5 minutes of Test app left", titles);
        Assert.Contains("2 minutes of Test app left", titles);
        Assert.All(notifications, item => Assert.Null(item.CountdownSeconds));
        // A reminder an absorbed child never notices is the same as no reminder, so these break
        // through Focus Assist too - without a countdown, because nothing is closing yet.
        Assert.All(warnings, item => Assert.True(item.IsUrgent));
        // One key for the whole restriction, so 5 minutes replaces 15 instead of stacking beside it.
        Assert.All(warnings, item => Assert.Equal($"reminder:app:{identity}", item.PersistentNotificationKey));
        // The message shown when the application opens is not one of those; it interrupts nothing.
        var opened = Assert.Single(notifications, item => item.Title == "Test app time");
        Assert.False(opened.IsUrgent);
        Assert.Null(opened.PersistentNotificationKey);
    }

    /// <summary>
    /// Feeds samples until the requested number of active seconds has been counted, the way the
    /// tray agent does, and returns every notification the coordinator produced along the way.
    /// </summary>
    private static async Task<List<UserNotification>> AccrueAsync(
        EnforcementCoordinator coordinator,
        ApplicationDescriptor app,
        int seconds)
    {
        var notifications = new List<UserNotification>();
        long sequence = 1;
        long elapsed = 0;

        // The first sample establishes the monotonic baseline and counts nothing, exactly as the
        // first sample after a service start does.
        var first = await coordinator.HandleSampleAsync(Sample(sequence, elapsed, 0, app), CancellationToken.None);
        notifications.AddRange(first.Notifications);

        var remaining = seconds;
        while (remaining > 0)
        {
            var step = Math.Min(30, remaining);
            remaining -= step;
            sequence++;
            elapsed += step * 1_000;
            var state = await coordinator.HandleSampleAsync(Sample(sequence, elapsed, 0, app), CancellationToken.None);
            notifications.AddRange(state.Notifications);
        }
        return notifications;
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 11, TimeZoneId = "UTC", DailyLimitSeconds = 1000 });
        var notifications = await AccrueAsync(coordinator, app, 60);

        var warning = Assert.Single(notifications, item => item.Title == "15 minutes of PC time left");
        Assert.Contains("signs you out", warning.Message);
        Assert.Null(warning.CountdownSeconds);
        Assert.True(warning.IsUrgent);
        Assert.Equal("reminder:pc", warning.PersistentNotificationKey);
    }

    [Fact]
    public async Task Only_final_enforcement_warnings_have_a_persistent_countdown()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var clock = new TrustedClock();
        clock.Synchronize(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 12, TimeZoneId = "UTC" });
        coordinator.NotifyApplicationClosing(
            "test-app",
            "Test app",
            new RuleDecision(false, BlockReason.ManualBlock, "Test app is blocked."),
            60);

        var appWarning = await coordinator.HandleSampleAsync(
            new SessionUsageSample(1, 0, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        var appNotification = Assert.Single(appWarning.Notifications);
        Assert.True(appNotification.IsUrgent);
        Assert.Equal(60, appNotification.CountdownSeconds);
        Assert.Equal("application:test-app", appNotification.PersistentNotificationKey);
        Assert.False(appNotification.DismissPersistentNotification);

        coordinator.DismissApplicationClosing("test-app");
        var dismissal = await coordinator.HandleSampleAsync(
            new SessionUsageSample(2, 1_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        var appDismissal = Assert.Single(dismissal.Notifications);
        Assert.True(appDismissal.DismissPersistentNotification);
        Assert.Equal("application:test-app", appDismissal.PersistentNotificationKey);

        coordinator.NotifyPcSignOut(
            new RuleDecision(false, BlockReason.DailyLimitReached, "The daily limit is reached."),
            60);
        var pcWarning = await coordinator.HandleSampleAsync(
            new SessionUsageSample(3, 2_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        var pcNotification = Assert.Single(pcWarning.Notifications);
        Assert.True(pcNotification.IsUrgent);
        Assert.Equal(60, pcNotification.CountdownSeconds);
        Assert.Equal("pc-sign-out", pcNotification.PersistentNotificationKey);

        coordinator.DismissPcSignOut();
        var pcDismissal = await coordinator.HandleSampleAsync(
            new SessionUsageSample(4, 3_000, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        var pcDismissalNotification = Assert.Single(pcDismissal.Notifications);
        Assert.True(pcDismissalNotification.DismissPersistentNotification);
        Assert.Equal("pc-sign-out", pcDismissalNotification.PersistentNotificationKey);
    }

    [Fact]
    public async Task A_delayed_final_warning_states_the_time_that_is_actually_left()
    {
        // The service starts counting down the moment it queues the warning, but the child only
        // sees it on the next sample. Restating the seconds on the way out is what keeps the card
        // and the sign-out talking about the same instant.
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 20, TimeZoneId = "UTC" });
        coordinator.NotifyPcSignOut(
            new RuleDecision(false, BlockReason.DailyLimitReached, "The daily limit is reached."),
            60);

        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        var state = await coordinator.HandleSampleAsync(
            new SessionUsageSample(1, 0, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        var warning = Assert.Single(state.Notifications);
        Assert.NotNull(warning.CountdownSeconds);
        Assert.InRange(warning.CountdownSeconds.Value, 50, 59);
        // The wording carries the countdown too, so both have to be rewritten together.
        Assert.Contains($"{warning.CountdownSeconds} seconds", warning.Title);
    }

    [Fact]
    public async Task A_final_warning_whose_deadline_has_passed_is_not_shown()
    {
        Directory.CreateDirectory(_directory);
        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        var coordinator = new EnforcementCoordinator(store, new TrustedClock(), Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(new DeviceRuleSnapshot { Revision = 21, TimeZoneId = "UTC" });
        coordinator.NotifyApplicationClosing(
            "test-app",
            "Test app",
            new RuleDecision(false, BlockReason.ManualBlock, "Test app is blocked."),
            1);

        await Task.Delay(TimeSpan.FromMilliseconds(1_200));
        var state = await coordinator.HandleSampleAsync(
            new SessionUsageSample(1, 0, false, 0, Environment.ProcessId, "Desktop", null),
            CancellationToken.None);

        // The application has already been closed by now; a card counting zero down would only
        // describe something that already happened.
        Assert.Empty(state.Notifications);
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
        var coordinator = new EnforcementCoordinator(store, clock, Extensions(store), ConnectedStatus(), NullLogger<EnforcementCoordinator>.Instance);
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
            ServerConnectionState.Connected,
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

    private static TimeExtensionService Extensions(LocalStore store) =>
        new(store, NullLogger<TimeExtensionService>.Instance);

    /// <summary>
    /// A PC that is reaching the server, which is the ordinary state these tests are about. The
    /// coordinator consults it only before explaining the household's web filtering, because a
    /// home network that is down produces the same browser error page as a blocked site.
    /// </summary>
    private static AgentRuntimeStatus ConnectedStatus()
    {
        var status = new AgentRuntimeStatus();
        status.MarkSynchronizationSucceeded();
        return status;
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

    private static ApplicationDescriptor CallDescriptor() => new()
    {
        DisplayName = "Call app",
        ExecutableName = "call.exe",
        ExecutablePath = @"C:\Apps\call.exe",
        ProductName = "Call app",
        OriginalFilename = "call.exe",
        SignaturePublisher = "Call Publisher"
    };

    private static SessionUsageSample Sample(long sequence, long elapsedMs, int idleSeconds, ApplicationDescriptor app) =>
        new(sequence, elapsedMs, idleSeconds >= 300, idleSeconds, Environment.ProcessId, "Test", app);
}
