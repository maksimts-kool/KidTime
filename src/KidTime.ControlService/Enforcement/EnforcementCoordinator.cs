using System.Collections.Concurrent;
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
                _applicationOpenedNotifications.Clear();
            if (_notificationDate != localDate || _notificationRuleRevision != rules.Revision)
            {
                _restrictionRemaining.Clear();
                _notificationDate = localDate;
                _notificationRuleRevision = rules.Revision;
            }
            string? identity = null;
            if (sample.ForegroundApplication is not null
                && ApplicationCatalogPolicy.IsUserManageable(sample.ForegroundApplication))
            {
                var foregroundApplication = ApplicationCatalogPolicy.NormalizeForCatalog(sample.ForegroundApplication);
                identity = ApplicationIdentity.CreateKey(foregroundApplication);
                _foregroundName = foregroundApplication.DisplayName;
                _foregroundIdentity = identity;
                await store.UpsertApplicationAsync(identity, foregroundApplication, cancellationToken);
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
                    await store.AddUsageAsync(localDate, null, seconds, cancellationToken);
                    if (identity is not null)
                        await store.AddUsageAsync(localDate, identity, seconds, cancellationToken);
                }
            }

            var pcUsage = await store.GetUsageAsync(localDate, null, cancellationToken);
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
                var appUsage = await store.GetUsageAsync(localDate, identity, cancellationToken);
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
            var usage = await store.GetUsageAsync(localDate, null, cancellationToken);
            return new PcEnforcementStatus(
                RuleEvaluator.EvaluateDevice(rules, utcNow, usage),
                usage,
                rules.DailyLimitSeconds);
        }
        finally { _gate.Release(); }
    }

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
            var usage = await store.GetUsageAsync(date, identityKey, cancellationToken);
            var decision = RuleEvaluator.EvaluateApplication(appRule, now, rules.TimeZoneId, usage);
            var episodeKey = $"{appRule.UpdatedAtUtc.UtcTicks}|{date:yyyy-MM-dd}|{decision.Reason}|{decision.AvailableAtUtc?.UtcTicks}";
            return new ApplicationEnforcementStatus(identityKey, appRule.DisplayName, decision, episodeKey);
        }
        finally { _gate.Release(); }
    }

    public void NotifyApplicationClosing(
        string identityKey,
        string displayName,
        RuleDecision decision,
        int graceSeconds)
    {
        _pendingApplicationLimitChanges.TryRemove(identityKey, out var limitChange);
        var grace = $"You have {FormatCountdown(graceSeconds)} to save your work.";
        var changed = limitChange is null ? string.Empty : $"{limitChange.Message} ";
        _notifications.Enqueue(new UserNotification(
            $"{displayName} closing in {FormatCountdown(graceSeconds)}",
            $"{changed}{BuildNotificationMessage(decision)} {grace} {displayName} will close automatically.",
            graceSeconds,
            ApplicationWarningKey(identityKey)));
        logger.LogWarning("Application {Application} is blocked for {Reason}; {GraceSeconds}-second save period started.",
            displayName, decision.Reason, graceSeconds);
    }

    public void DismissApplicationClosing(string identityKey) =>
        _notifications.Enqueue(new UserNotification(
            string.Empty,
            string.Empty,
            PersistentNotificationKey: ApplicationWarningKey(identityKey),
            DismissPersistentNotification: true));

    public void NotifyPcSignOut(string message, int countdownSeconds) =>
        _notifications.Enqueue(new UserNotification(
            "PC sign-out required",
            message,
            countdownSeconds,
            "pc-sign-out"));

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
            $"You can use this PC again. It was locked because: {previousDecision.Message}"));
    }

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
        if (oldDailyLimit != newDailyLimit)
            parts.Add($"daily time changed from {FormatLimit(oldDailyLimit)} to {FormatLimit(newDailyLimit)}");
        if (!SchedulesEqual(oldSchedule, newSchedule)) parts.Add("allowed schedule changed");
        return $"{scope}: {string.Join("; ", parts)}. The new limit applies now.";
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

    private static string BuildNotificationMessage(RuleDecision decision) =>
        decision.AvailableAtUtc is { } available
            ? $"{decision.Message} Available again {available.ToLocalTime():ddd, HH:mm}."
            : decision.Message;

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
                signsOut ? "PC time warning" : $"{displayName} time warning",
                signsOut
                    ? $"{label} remaining. Windows will sign you out when the restriction is reached."
                    : $"{label} remaining. {displayName} will close when the restriction is reached."));
            logger.LogInformation("{Scope} restriction warning queued with {Seconds} seconds remaining.",
                displayName, threshold);
        }

        _restrictionRemaining[key] = restriction.RemainingSeconds;
    }

    private static UserNotification BuildApplicationOpenedNotification(string displayName, TimeRestriction restriction)
    {
        var details = new List<string>();
        if (restriction.ActiveSecondsRemaining is int activeRemaining)
            details.Add($"{FormatRemaining(activeRemaining)} of active time remains today.");
        if (restriction.ScheduleEndUtc is { } scheduleEnd)
            details.Add($"Scheduled access ends {FormatDeadline(scheduleEnd)}.");
        details.Add(restriction.ActiveSecondsRemaining is not null && restriction.ScheduleEndUtc is not null
            ? "The app will close when the first restriction is reached."
            : "The app will close when this restriction is reached.");
        return new UserNotification($"{displayName} time available", string.Join(" ", details));
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
