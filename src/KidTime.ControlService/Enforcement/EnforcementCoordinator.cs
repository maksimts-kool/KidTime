using System.Collections.Concurrent;
using System.Diagnostics;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Enforcement;

public sealed record PcEnforcementStatus(
    RuleDecision Decision,
    int TodayActiveSeconds,
    int? DailyLimitSeconds);

public sealed record ApplicationEnforcementStatus(
    string IdentityKey,
    string DisplayName,
    RuleDecision Decision,
    string EpisodeKey);

public sealed class EnforcementCoordinator(
    LocalStore store,
    TrustedClock clock,
    ILogger<EnforcementCoordinator> logger)
{
    private static readonly int[] WarningThresholdSeconds = [15 * 60, 5 * 60, 2 * 60];
    private static readonly TimeSpan UsageFlushInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ApplicationRefreshInterval = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceRuleSnapshot _rules = new();
    private long _lastSequence;
    private long _lastMonotonicMilliseconds;
    private long _millisecondRemainder;
    private string? _foregroundName;
    private string? _foregroundIdentity;
    private string? _loggedForegroundIdentity;
    private bool _lastPcBlocked;
    private readonly ConcurrentQueue<UserNotification> _notifications = new();
    private readonly ConcurrentDictionary<string, UserNotification> _pendingApplicationLimitChanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _restrictionRemaining = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _applicationOpenedNotifications = new(StringComparer.Ordinal);
    private DateOnly? _notificationDate;
    private long _notificationRuleRevision = -1;

    // This service is the only writer of the local usage table, so it can hold the running
    // totals in memory. Every evaluation - one per second from the lockout service, one per
    // running application from the process monitor, one per sample from the agent - used to
    // open a SQLite connection; now they read memory and the accumulated seconds are written
    // back in one batch. A crash can lose at most UsageFlushInterval of counted time.
    private readonly Dictionary<UsageKey, int> _usageTotals = [];
    private readonly Dictionary<UsageKey, int> _unflushedUsage = [];
    private readonly Dictionary<string, DateTimeOffset> _applicationRefreshed = new(StringComparer.Ordinal);
    private long _lastUsageFlushTimestamp = Stopwatch.GetTimestamp();

    public DeviceRuleSnapshot Rules => _rules;
    public string? ForegroundName => _foregroundName;
    public string? ForegroundIdentity => _foregroundIdentity;

    public void UpdateRules(DeviceRuleSnapshot rules)
    {
        var previous = Interlocked.Exchange(ref _rules, rules);
        if (previous.DeviceId != Guid.Empty && rules.Revision != previous.Revision)
            QueueRuleChangeNotifications(previous, rules);
        logger.LogInformation("Rules updated to revision {Revision}.", rules.Revision);
    }

    public async Task<EnforcementState> HandleSampleAsync(SessionUsageSample sample, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = _rules;
            var utcNow = clock.GetUtcNow();
            var localDate = RuleEvaluator.GetLocalDate(utcNow, rules.TimeZoneId);
            if (_notificationDate != localDate)
            {
                _applicationOpenedNotifications.Clear();
                _usageTotals.Clear();
                _applicationRefreshed.Clear();
            }
            if (_notificationDate != localDate || _notificationRuleRevision != rules.Revision)
            {
                _restrictionRemaining.Clear();
                _notificationDate = localDate;
                _notificationRuleRevision = rules.Revision;
            }
            string? identity = null;
            ApplicationDescriptor? foregroundApplication = null;
            if (sample.ForegroundApplication is not null
                && ApplicationCatalogPolicy.IsUserManageable(sample.ForegroundApplication))
            {
                foregroundApplication = ApplicationCatalogPolicy.NormalizeForCatalog(sample.ForegroundApplication);
                identity = ApplicationIdentity.CreateKey(foregroundApplication);
                _foregroundName = foregroundApplication.DisplayName;
                _foregroundIdentity = identity;
            }
            else
            {
                _foregroundName = null;
                _foregroundIdentity = null;
            }

            var foregroundChanged = !string.Equals(identity, _loggedForegroundIdentity, StringComparison.Ordinal);
            if (foregroundChanged)
            {
                logger.LogInformation("Foreground application changed to {Application} ({IdentityKey}).",
                    _foregroundName ?? "none", identity ?? "none");
                _loggedForegroundIdentity = identity;
            }

            // The descriptor only changes when the application is updated or moved, so writing it
            // on every two-second sample was pure disk traffic on the controlled PC.
            if (identity is not null && foregroundApplication is not null
                && (foregroundChanged
                    || !_applicationRefreshed.TryGetValue(identity, out var refreshed)
                    || utcNow - refreshed >= ApplicationRefreshInterval))
            {
                await store.UpsertApplicationAsync(identity, foregroundApplication, cancellationToken);
                _applicationRefreshed[identity] = utcNow;
            }

            var deltaMilliseconds = sample.Sequence > _lastSequence && sample.MonotonicElapsedMilliseconds >= _lastMonotonicMilliseconds
                ? Math.Min(30_000, sample.MonotonicElapsedMilliseconds - _lastMonotonicMilliseconds)
                : 0;
            _lastSequence = sample.Sequence;
            _lastMonotonicMilliseconds = sample.MonotonicElapsedMilliseconds;
            var isIdle = sample.IdleSeconds >= rules.IdleThresholdSeconds;
            if (!isIdle && sample.ProcessId > 0 && deltaMilliseconds > 0)
            {
                _millisecondRemainder += deltaMilliseconds;
                var seconds = (int)(_millisecondRemainder / 1000);
                _millisecondRemainder %= 1000;
                if (seconds > 0)
                {
                    await AddUsageAsync(localDate, null, seconds, cancellationToken);
                    if (identity is not null)
                        await AddUsageAsync(localDate, identity, seconds, cancellationToken);
                }
            }

            if (Stopwatch.GetElapsedTime(_lastUsageFlushTimestamp) >= UsageFlushInterval)
                await FlushUsageCoreAsync(cancellationToken);

            var pcUsage = await ReadUsageAsync(localDate, null, cancellationToken);
            var pcDecision = RuleEvaluator.EvaluateDevice(rules, utcNow, pcUsage);
            if (!pcDecision.IsAllowed && !_lastPcBlocked)
                logger.LogWarning("PC blocked; reason {Reason}.", pcDecision.Reason);
            else if (pcDecision.IsAllowed && _lastPcBlocked)
                logger.LogInformation("PC unblocked.");
            _lastPcBlocked = !pcDecision.IsAllowed;
            if (pcDecision.IsAllowed && BuildRestriction(
                    rules.DailyLimitSeconds, pcUsage, rules.Schedule, utcNow, rules.TimeZoneId, localDate) is { } pcRestriction)
            {
                QueueThresholdWarnings("pc", "PC", pcRestriction, signsOut: true);
            }
            RuleDecision? appDecision = null;
            if (identity is not null && rules.Applications.FirstOrDefault(x => x.IdentityKey == identity) is { } appRule)
            {
                var appUsage = await ReadUsageAsync(localDate, identity, cancellationToken);
                appDecision = RuleEvaluator.EvaluateApplication(appRule, utcNow, rules.TimeZoneId, appUsage);
                if (appDecision.IsAllowed && BuildRestriction(
                             appRule.DailyLimitSeconds, appUsage, appRule.Schedule, utcNow, rules.TimeZoneId, localDate) is { } appRestriction)
                {
                    var scope = $"app:{identity}";
                    if (foregroundChanged
                        && (!_applicationOpenedNotifications.TryGetValue(identity, out var lastShown)
                            || utcNow - lastShown >= TimeSpan.FromMinutes(5)))
                    {
                        _notifications.Enqueue(BuildApplicationOpenedNotification(appRule.DisplayName, appRestriction));
                        _applicationOpenedNotifications[identity] = utcNow;
                        _restrictionRemaining[$"{scope}|{appRestriction.Key}"] = appRestriction.RemainingSeconds;
                    }
                    QueueThresholdWarnings(scope, appRule.DisplayName, appRestriction, signsOut: false);
                }
                if (appDecision.IsAllowed
                    && _pendingApplicationLimitChanges.TryRemove(identity, out var changedNotification))
                    _notifications.Enqueue(changedNotification);
            }

            _notifications.TryDequeue(out var notification);
            var remaining = rules.DailyLimitSeconds is int limit ? Math.Max(0, limit - pcUsage) : -1;
            return new EnforcementState(!pcDecision.IsAllowed, pcDecision, appDecision, pcUsage,
                rules.DailyLimitSeconds, remaining, notification);
        }
        finally { _gate.Release(); }
    }

    public async Task<PcEnforcementStatus> GetPcStatusAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = _rules;
            var utcNow = clock.GetUtcNow();
            var localDate = RuleEvaluator.GetLocalDate(utcNow, rules.TimeZoneId);
            var usage = await ReadUsageAsync(localDate, null, cancellationToken);
            return new PcEnforcementStatus(
                RuleEvaluator.EvaluateDevice(rules, utcNow, usage),
                usage,
                rules.DailyLimitSeconds);
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionStatusSnapshot> GetUserStatusAsync(
        ServerConnectionStatus server,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = _rules;
            var now = clock.GetUtcNow();
            var localDate = RuleEvaluator.GetLocalDate(now, rules.TimeZoneId);
            var pcUsage = await ReadUsageAsync(localDate, null, cancellationToken);
            var pcDecision = RuleEvaluator.EvaluateDevice(rules, now, pcUsage);
            var screenTime = BuildAllowanceStatus(
                pcDecision,
                pcUsage,
                rules.DailyLimitSeconds,
                rules.Schedule,
                now,
                rules.TimeZoneId);

            var applications = new List<ApplicationTimeStatus>();
            foreach (var rule in rules.Applications
                         .Where(item => item.ManuallyBlocked
                                        || item.DailyLimitSeconds is not null
                                        || item.Schedule.IsConfigured)
                         .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var usage = await ReadUsageAsync(localDate, rule.IdentityKey, cancellationToken);
                var decision = RuleEvaluator.EvaluateApplication(rule, now, rules.TimeZoneId, usage);
                applications.Add(new ApplicationTimeStatus(
                    rule.IdentityKey,
                    rule.DisplayName,
                    rule.ManuallyBlocked,
                    BuildAllowanceStatus(
                        decision,
                        usage,
                        rule.DailyLimitSeconds,
                        rule.Schedule,
                        now,
                        rules.TimeZoneId)));
            }

            return new SessionStatusSnapshot(
                now,
                rules.ControlledUserName,
                rules.Revision,
                server,
                screenTime,
                applications);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Writes buffered active seconds to the local database. The sync loop calls this on every
    /// pass and once more while shutting down, so counted time survives a service restart.
    /// </summary>
    public async Task FlushUsageAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await FlushUsageCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task FlushUsageCoreAsync(CancellationToken cancellationToken)
    {
        _lastUsageFlushTimestamp = Stopwatch.GetTimestamp();
        if (_unflushedUsage.Count == 0) return;
        var pending = _unflushedUsage.ToList();
        _unflushedUsage.Clear();
        try
        {
            foreach (var (key, seconds) in pending)
                await store.AddUsageAsync(key.LocalDate, key.IdentityKey, seconds, cancellationToken);
        }
        catch
        {
            // Keep the seconds buffered so a transient database failure does not lose usage.
            foreach (var (key, seconds) in pending)
                _unflushedUsage[key] = _unflushedUsage.GetValueOrDefault(key) + seconds;
            throw;
        }
    }

    private async Task<int> ReadUsageAsync(DateOnly localDate, string? identityKey, CancellationToken cancellationToken)
    {
        var key = new UsageKey(localDate, identityKey);
        if (_usageTotals.TryGetValue(key, out var total)) return total;
        total = await store.GetUsageAsync(localDate, identityKey, cancellationToken);
        _usageTotals[key] = total;
        return total;
    }

    private async Task AddUsageAsync(
        DateOnly localDate,
        string? identityKey,
        int seconds,
        CancellationToken cancellationToken)
    {
        var key = new UsageKey(localDate, identityKey);
        _usageTotals[key] = await ReadUsageAsync(localDate, identityKey, cancellationToken) + seconds;
        _unflushedUsage[key] = _unflushedUsage.GetValueOrDefault(key) + seconds;
    }

    private readonly record struct UsageKey(DateOnly LocalDate, string? IdentityKey);

    public async Task<RuleDecision> EvaluateApplicationAsync(string identityKey, CancellationToken cancellationToken)
        => (await EvaluateApplicationStatusAsync(identityKey, cancellationToken))?.Decision ?? RuleDecision.Allowed;

    public async Task<ApplicationEnforcementStatus?> EvaluateApplicationStatusAsync(
        string identityKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = _rules;
            var appRule = rules.Applications.FirstOrDefault(x => x.IdentityKey == identityKey);
            if (appRule is null) return null;
            var now = clock.GetUtcNow();
            var date = RuleEvaluator.GetLocalDate(now, rules.TimeZoneId);
            var usage = await ReadUsageAsync(date, identityKey, cancellationToken);
            var decision = RuleEvaluator.EvaluateApplication(appRule, now, rules.TimeZoneId, usage);
            var episodeKey = $"{appRule.UpdatedAtUtc.UtcTicks}|{date:yyyy-MM-dd}|{decision.Reason}|{decision.AvailableAtUtc?.UtcTicks}";
            return new ApplicationEnforcementStatus(identityKey, appRule.DisplayName, decision, episodeKey);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The urgent toast shown right before an application is closed. It stays on screen and
    /// interrupts, so it carries only what has to be acted on: what is closing, how long is
    /// left, and why. Detail belongs in the screen-time window, not in an interruption.
    /// </summary>
    public void NotifyApplicationClosing(
        string identityKey,
        string displayName,
        RuleDecision decision,
        int graceSeconds)
    {
        _pendingApplicationLimitChanges.TryRemove(identityKey, out _);
        _notifications.Enqueue(new UserNotification(
            $"{displayName} closes in {FormatCountdown(graceSeconds)}",
            $"{ShortReason(decision)}. Save your work now.",
            graceSeconds,
            ApplicationWarningKey(identityKey),
            IsUrgent: true));
        logger.LogWarning("Application {Application} is blocked for {Reason}; {GraceSeconds}-second save period started.",
            displayName, decision.Reason, graceSeconds);
    }

    public void DismissApplicationClosing(string identityKey) =>
        _notifications.Enqueue(new UserNotification(
            string.Empty,
            string.Empty,
            PersistentNotificationKey: ApplicationWarningKey(identityKey),
            DismissPersistentNotification: true));

    public void NotifyPcSignOut(RuleDecision decision, int countdownSeconds) =>
        _notifications.Enqueue(new UserNotification(
            $"Signing out in {FormatCountdown(countdownSeconds)}",
            $"{ShortReason(decision)}. Save your work now.",
            countdownSeconds,
            "pc-sign-out",
            IsUrgent: true));

    public void DismissPcSignOut() =>
        _notifications.Enqueue(new UserNotification(
            string.Empty,
            string.Empty,
            PersistentNotificationKey: "pc-sign-out",
            DismissPersistentNotification: true));

    public void NotifyPcAvailable(RuleDecision previousDecision)
    {
        _notifications.Enqueue(new UserNotification(
            "PC available",
            $"You can use this PC again. Earlier: {ShortReason(previousDecision).ToLowerInvariant()}."));
    }

    /// <summary>Announces a completed background update. Informational, never urgent.</summary>
    public void NotifyServicesUpdated(string version) =>
        _notifications.Enqueue(new UserNotification(
            "KidTime updated",
            $"KidTime is now version {version}. Nothing changes for you."));

    private void QueueRuleChangeNotifications(DeviceRuleSnapshot previous, DeviceRuleSnapshot current)
    {
        if (LimitsDiffer(previous.DailyLimitSeconds, previous.Schedule, current.DailyLimitSeconds, current.Schedule))
        {
            _notifications.Enqueue(new UserNotification(
                "PC time limit changed",
                BuildLimitChangeMessage("PC", previous.DailyLimitSeconds, previous.Schedule,
                    current.DailyLimitSeconds, current.Schedule)));
        }

        var foregroundIdentity = _foregroundIdentity;
        if (foregroundIdentity is null) return;
        var oldApp = previous.Applications.FirstOrDefault(item => item.IdentityKey == foregroundIdentity);
        var newApp = current.Applications.FirstOrDefault(item => item.IdentityKey == foregroundIdentity);
        if (oldApp is null || newApp is null
            || !LimitsDiffer(oldApp.DailyLimitSeconds, oldApp.Schedule, newApp.DailyLimitSeconds, newApp.Schedule))
            return;
        _pendingApplicationLimitChanges[foregroundIdentity] = new UserNotification(
            $"{newApp.DisplayName} time limit changed",
            BuildLimitChangeMessage(newApp.DisplayName, oldApp.DailyLimitSeconds, oldApp.Schedule,
                newApp.DailyLimitSeconds, newApp.Schedule));
    }

    private static bool LimitsDiffer(
        int? oldDailyLimit,
        WeeklySchedule oldSchedule,
        int? newDailyLimit,
        WeeklySchedule newSchedule) =>
        oldDailyLimit != newDailyLimit || !SchedulesEqual(oldSchedule, newSchedule);

    private static bool SchedulesEqual(WeeklySchedule left, WeeklySchedule right)
    {
        var leftDays = left.Days.OrderBy(day => day.Day).ToList();
        var rightDays = right.Days.OrderBy(day => day.Day).ToList();
        if (leftDays.Count != rightDays.Count) return false;
        for (var index = 0; index < leftDays.Count; index++)
        {
            if (leftDays[index].Day != rightDays[index].Day) return false;
            var leftWindows = leftDays[index].Windows.OrderBy(window => window.Start).ThenBy(window => window.End).ToList();
            var rightWindows = rightDays[index].Windows.OrderBy(window => window.Start).ThenBy(window => window.End).ToList();
            if (!leftWindows.SequenceEqual(rightWindows)) return false;
        }
        return true;
    }

    private static string BuildLimitChangeMessage(
        string scope,
        int? oldDailyLimit,
        WeeklySchedule oldSchedule,
        int? newDailyLimit,
        WeeklySchedule newSchedule)
    {
        var parts = new List<string>();
        if (oldDailyLimit != newDailyLimit) parts.Add($"daily time is now {FormatLimit(newDailyLimit)}");
        if (!SchedulesEqual(oldSchedule, newSchedule)) parts.Add("the schedule changed");
        return $"{scope}: {string.Join(", ", parts)}.";
    }

    private static string FormatLimit(int? seconds) => seconds is null
        ? "no limit"
        : seconds.Value % 3600 == 0
            ? $"{seconds.Value / 3600}h"
            : $"{seconds.Value / 3600}h {(seconds.Value % 3600) / 60:00}m";

    private static string FormatCountdown(int seconds) => seconds >= 60
        ? $"{(int)Math.Ceiling(seconds / 60d)} minute{(seconds > 60 ? "s" : string.Empty)}"
        : $"{seconds} seconds";

    private static string ApplicationWarningKey(string identityKey) => $"application:{identityKey}";

    private static string ShortReason(RuleDecision decision) => decision.Reason switch
    {
        BlockReason.ManualBlock => "Your parent blocked it",
        BlockReason.DailyLimitReached => "Daily time is used up",
        BlockReason.OutsideAllowedSchedule => "Outside allowed hours",
        _ => "Not available right now"
    };

    private void QueueThresholdWarnings(string scope, string displayName, TimeRestriction restriction, bool signsOut)
    {
        var key = $"{scope}|{restriction.Key}";
        if (!_restrictionRemaining.TryGetValue(key, out var previous))
        {
            _restrictionRemaining[key] = restriction.RemainingSeconds;
            return;
        }

        foreach (var threshold in WarningThresholdSeconds)
        {
            if (previous <= threshold || restriction.RemainingSeconds > threshold) continue;
            var label = threshold >= 60 ? $"{threshold / 60} minutes" : $"{threshold} seconds";
            _notifications.Enqueue(new UserNotification(
                signsOut ? $"{label} of PC time left" : $"{label} of {displayName} left",
                signsOut ? "Windows signs you out when it runs out." : $"{displayName} closes when it runs out."));
            logger.LogInformation("{Scope} restriction warning queued with {Seconds} seconds remaining.",
                displayName, threshold);
        }

        _restrictionRemaining[key] = restriction.RemainingSeconds;
    }

    private static UserNotification BuildApplicationOpenedNotification(string displayName, TimeRestriction restriction)
    {
        var message = (restriction.ActiveSecondsRemaining, restriction.ScheduleEndUtc) switch
        {
            (int remaining, { } scheduleEnd) => $"{FormatRemaining(remaining)} left, until {FormatDeadline(scheduleEnd)}.",
            (int remaining, null) => $"{FormatRemaining(remaining)} left today.",
            (null, { } scheduleEnd) => $"Available until {FormatDeadline(scheduleEnd)}.",
            _ => "Time limited."
        };
        return new UserNotification($"{displayName} time", message);
    }

    private static TimeRestriction? BuildRestriction(
        int? dailyLimitSeconds,
        int activeSeconds,
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId,
        DateOnly localDate)
    {
        int? activeRemaining = dailyLimitSeconds is int limit ? Math.Max(0, limit - activeSeconds) : null;
        var scheduleEnd = RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, utcNow, timeZoneId);
        int? scheduleRemaining = scheduleEnd is { } end
            ? Math.Max(0, (int)Math.Ceiling((end - utcNow).TotalSeconds))
            : null;
        if (activeRemaining is null && scheduleRemaining is null) return null;

        var dailyWins = activeRemaining is not null
                        && (scheduleRemaining is null || activeRemaining <= scheduleRemaining);
        var remaining = dailyWins ? activeRemaining!.Value : scheduleRemaining!.Value;
        var key = dailyWins
            ? $"daily:{localDate:yyyy-MM-dd}"
            : $"schedule:{scheduleEnd!.Value.UtcTicks}";
        return new TimeRestriction(remaining, key, activeRemaining, scheduleEnd);
    }

    private static TimeAllowanceStatus BuildAllowanceStatus(
        RuleDecision decision,
        int activeSeconds,
        int? dailyLimitSeconds,
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId)
    {
        var isWithinSchedule = RuleEvaluator.IsWithinSchedule(schedule, utcNow, timeZoneId);
        var scheduleEnd = isWithinSchedule
            ? RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, utcNow, timeZoneId)
            : null;
        return new TimeAllowanceStatus(
            decision.IsAllowed,
            decision.Reason,
            decision.Message,
            activeSeconds,
            dailyLimitSeconds,
            dailyLimitSeconds is int limit ? Math.Max(0, limit - activeSeconds) : null,
            schedule.IsConfigured,
            isWithinSchedule,
            isWithinSchedule
                ? RuleEvaluator.FindCurrentAllowanceStartUtc(schedule, utcNow, timeZoneId)
                : null,
            scheduleEnd,
            !isWithinSchedule
                ? RuleEvaluator.FindNextAllowanceStartUtc(schedule, utcNow, timeZoneId)
                : null);
    }

    private static string FormatRemaining(int seconds)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(seconds / 60d));
        return minutes >= 60
            ? $"{minutes / 60}h {minutes % 60:00}m"
            : $"{minutes} minutes";
    }

    private static string FormatDeadline(DateTimeOffset deadline) =>
        deadline.ToLocalTime().Date == DateTimeOffset.Now.Date
            ? $"today at {deadline.ToLocalTime():HH:mm}"
            : deadline.ToLocalTime().ToString("ddd at HH:mm");

    private sealed record TimeRestriction(
        int RemainingSeconds,
        string Key,
        int? ActiveSecondsRemaining,
        DateTimeOffset? ScheduleEndUtc);
}
