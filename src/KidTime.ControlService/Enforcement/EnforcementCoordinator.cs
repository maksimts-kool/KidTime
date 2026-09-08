using System.Collections.Concurrent;
using System.Diagnostics;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Enforcement;

/// <summary>
/// The PC as the rules see it right now. <c>Pending</c> is what it is heading into, so the final
/// warning can be shown in the minute before screen time ends rather than the minute after; it is
/// null when nothing in the rules names a deadline the service could count down to.
/// </summary>
public sealed record PcEnforcementStatus(
    RuleDecision Decision,
    int TodayActiveSeconds,
    int? DailyLimitSeconds,
    PendingRestriction? Pending);

public sealed record ApplicationEnforcementStatus(
    string IdentityKey,
    string DisplayName,
    RuleDecision Decision,
    string EpisodeKey);

public sealed class EnforcementCoordinator(
    LocalStore store,
    TrustedClock clock,
    TimeExtensionService extensions,
    ILogger<EnforcementCoordinator> logger)
{
    private static readonly int[] WarningThresholdSeconds = [15 * 60, 5 * 60, 2 * 60];

    /// <summary>
    /// How long an accepted sample keeps the daily limit counting down. Samples arrive every two
    /// seconds, so this tolerates a hiccup while still knowing within a few seconds that the child
    /// has stopped spending the allowance - which is what makes a limit a deadline or not.
    /// </summary>
    private static readonly TimeSpan ActiveTimeFreshness = TimeSpan.FromSeconds(15);

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
    private readonly ConcurrentQueue<PendingNotification> _notifications = new();
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
    private long _lastCountedUsageTimestamp;

    /// <summary>
    /// When the PC's own schedule window closes, kept rather than worked out again.
    /// <see cref="RuleEvaluator.FindCurrentAllowanceEndUtc"/> walks the week a minute at a time,
    /// and the lockout loop now asks for it every second so it can warn the child before screen
    /// time ends rather than after. The answer is an absolute instant: it changes when the rules
    /// change, and when the window it names has passed. Nothing else can move it, so a kept one is
    /// exact rather than merely fresh.
    /// </summary>
    private (long Revision, DateTimeOffset ComputedAtUtc, DateTimeOffset? End)? _deviceAllowanceEnd;

    /// <summary>How long a "the schedule never closes" answer is trusted before being re-checked.</summary>
    private static readonly TimeSpan AllowanceEndRecheckInterval = TimeSpan.FromMinutes(1);

    public DeviceRuleSnapshot Rules => _rules;

    /// <summary>Wording for the language the parent chose for this PC.</summary>
    private AgentStrings Text => AgentStrings.For(_rules.Language);

    /// <summary>
    /// A queued message. A final warning also carries the monotonic instant the service will act
    /// on and the wording that goes with a number of seconds, because both the title and the
    /// countdown have to state the time left when the message reaches the child, not when the
    /// service decided to send it.
    /// </summary>
    private readonly record struct PendingNotification(
        UserNotification Notification,
        long? DeadlineTimestamp,
        Func<int, UserNotification>? Rewrite);

    private void Enqueue(UserNotification notification) =>
        _notifications.Enqueue(new PendingNotification(notification, null, null));

    /// <summary>
    /// Queues a final warning against the deadline the caller has just started. The caller owns
    /// that deadline; this is only what the child is told about it.
    /// </summary>
    private void EnqueueCountdown(Func<int, UserNotification> build, int countdownSeconds) =>
        _notifications.Enqueue(new PendingNotification(
            build(countdownSeconds),
            Stopwatch.GetTimestamp() + (long)(countdownSeconds * (double)Stopwatch.Frequency),
            build));

    /// <summary>
    /// Hands the agent everything that is waiting, restating each final warning in the seconds
    /// that are actually left. A warning used to arrive carrying the number it was born with, so
    /// the card counted that number down from whenever it appeared while the service counted from
    /// when it was queued - which is how a child watched five seconds remaining as the application
    /// closed. A warning whose deadline has already passed is dropped rather than drawn at zero;
    /// enforcement never depended on it being delivered.
    /// </summary>
    private List<UserNotification> DrainNotifications()
    {
        var delivered = new List<UserNotification>();
        while (_notifications.TryDequeue(out var pending))
        {
            if (pending.DeadlineTimestamp is not { } deadline || pending.Rewrite is not { } rewrite)
            {
                delivered.Add(pending.Notification);
                continue;
            }

            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), deadline);
            if (remaining <= TimeSpan.Zero)
            {
                logger.LogWarning("A final warning expired before it could be shown to the user.");
                continue;
            }

            delivered.Add(rewrite((int)Math.Ceiling(remaining.TotalSeconds)));
        }

        return delivered;
    }

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
                _lastCountedUsageTimestamp = Stopwatch.GetTimestamp();
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
            // Extra time a parent granted today is part of the limit from here on. Reading the
            // parent's raw number anywhere below would close the child down at the old limit
            // while the window told them they had been given more.
            var pcLimit = RuleEvaluator.EffectiveDailyLimitSeconds(rules.DailyLimitSeconds, rules.Bonus, localDate);
            var pcDecision = RuleEvaluator.EvaluateDevice(rules, utcNow, pcUsage);
            if (!pcDecision.IsAllowed && !_lastPcBlocked)
                logger.LogWarning("PC blocked; reason {Reason}.", pcDecision.Reason);
            else if (pcDecision.IsAllowed && _lastPcBlocked)
                logger.LogInformation("PC unblocked.");
            _lastPcBlocked = !pcDecision.IsAllowed;
            var offers = new List<TimeExtensionOffer>(2);
            if (pcDecision.IsAllowed && BuildRestriction(
                    pcLimit, pcUsage, CurrentDeviceAllowanceEnd(rules, utcNow), utcNow, localDate) is { } pcRestriction)
            {
                // The display name reaches only the application wording and the log line, so the
                // PC path keeps the stable English label.
                QueueThresholdWarnings("pc", "PC", pcRestriction, signsOut: true);
            }

            // Deliberately outside the branch above: that one only runs while the PC is still
            // allowed, and the moment a child most wants to ask is the moment the time ran out
            // and the sign-out card appeared. The offer survives every block, including the two
            // a grant now lifts from the moment it is given.
            if (await BuildOfferAsync(localDate, null, Text.PcScopeName, pcLimit, pcUsage, pcDecision,
                    rules.Schedule, utcNow, rules.TimeZoneId, cancellationToken) is { } pcOffer)
                offers.Add(pcOffer);

            RuleDecision? appDecision = null;
            if (identity is not null && rules.Applications.FirstOrDefault(x => x.IdentityKey == identity) is { } appRule)
            {
                var appUsage = await ReadUsageAsync(localDate, identity, cancellationToken);
                var appLimit = RuleEvaluator.EffectiveDailyLimitSeconds(
                    appRule.DailyLimitSeconds, appRule.Bonus, localDate);
                appDecision = RuleEvaluator.EvaluateApplication(appRule, utcNow, rules.TimeZoneId, appUsage, rules.Language);
                if (appDecision.IsAllowed && BuildRestriction(
                             appLimit, appUsage,
                             RuleEvaluator.FindCurrentAllowanceEndUtc(appRule.Schedule, utcNow, rules.TimeZoneId),
                             utcNow, localDate) is { } appRestriction)
                {
                    var scope = $"app:{identity}";
                    if (foregroundChanged
                        && (!_applicationOpenedNotifications.TryGetValue(identity, out var lastShown)
                            || utcNow - lastShown >= TimeSpan.FromMinutes(5)))
                    {
                        Enqueue(BuildApplicationOpenedNotification(Text, appRule.DisplayName, appRestriction));
                        _applicationOpenedNotifications[identity] = utcNow;
                        _restrictionRemaining[$"{scope}|{appRestriction.Key}"] = appRestriction.RemainingSeconds;
                    }
                    QueueThresholdWarnings(scope, appRule.DisplayName, appRestriction, signsOut: false);
                }

                // Only the application in the foreground is offered extra time here - a child
                // asks about what is closing on them, they do not shop through a list - and the
                // offer outlives the block for the same reason the PC's does.
                if (await BuildOfferAsync(localDate, identity, appRule.DisplayName, appLimit, appUsage, appDecision,
                        appRule.Schedule, utcNow, rules.TimeZoneId, cancellationToken) is { } appOffer)
                    offers.Add(appOffer);
                if (appDecision.IsAllowed
                    && _pendingApplicationLimitChanges.TryRemove(identity, out var changedNotification))
                    Enqueue(changedNotification);
            }

            var remaining = pcLimit is int limit ? Math.Max(0, limit - pcUsage) : -1;
            return new EnforcementState(!pcDecision.IsAllowed, pcDecision, appDecision, pcUsage,
                pcLimit, remaining, DrainNotifications(), Language: rules.Language,
                ExtensionOffers: offers);
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
                RuleEvaluator.EffectiveDailyLimitSeconds(rules.DailyLimitSeconds, rules.Bonus, localDate),
                RuleEvaluator.FindPendingDeviceRestriction(
                    rules, utcNow, usage, IsSpendingActiveTime, CurrentDeviceAllowanceEnd(rules, utcNow)));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Caller holds the gate.</summary>
    private DateTimeOffset? CurrentDeviceAllowanceEnd(DeviceRuleSnapshot rules, DateTimeOffset utcNow)
    {
        if (_deviceAllowanceEnd is { } kept
            && kept.Revision == rules.Revision
            && (kept.End is { } end
                ? end > utcNow
                : utcNow - kept.ComputedAtUtc < AllowanceEndRecheckInterval))
            return kept.End;

        var computed = RuleEvaluator.FindCurrentAllowanceEndUtc(rules.Schedule, utcNow, rules.TimeZoneId);
        _deviceAllowanceEnd = (rules.Revision, utcNow, computed);
        return computed;
    }

    /// <summary>
    /// Whether the child is spending the daily allowance right now. A limit only counts down while
    /// accepted foreground samples are arriving, so it is a deadline while they are and no
    /// deadline at all while the PC sits idle.
    /// </summary>
    private bool IsSpendingActiveTime =>
        _lastCountedUsageTimestamp != 0
        && Stopwatch.GetElapsedTime(_lastCountedUsageTimestamp) < ActiveTimeFreshness;

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
                rules.Bonus,
                rules.Schedule,
                now,
                localDate,
                rules.TimeZoneId);

            var applications = new List<ApplicationTimeStatus>();
            foreach (var rule in rules.Applications
                         .Where(item => item.ManuallyBlocked
                                        || item.DailyLimitSeconds is not null
                                        || item.Schedule.IsConfigured)
                         .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var usage = await ReadUsageAsync(localDate, rule.IdentityKey, cancellationToken);
                var decision = RuleEvaluator.EvaluateApplication(rule, now, rules.TimeZoneId, usage, rules.Language);
                // Every limited application is offered extra time on its own card, not just the
                // one in the foreground: a child looking at the Apps tab is looking at exactly the
                // list of things they might need more time for. This is the snapshot the window
                // asks for while it is open, so the extra work is bounded by that.
                var limit = RuleEvaluator.EffectiveDailyLimitSeconds(rule.DailyLimitSeconds, rule.Bonus, localDate);
                applications.Add(new ApplicationTimeStatus(
                    rule.IdentityKey,
                    rule.DisplayName,
                    rule.ManuallyBlocked,
                    BuildAllowanceStatus(
                        decision,
                        usage,
                        rule.DailyLimitSeconds,
                        rule.Bonus,
                        rule.Schedule,
                        now,
                        localDate,
                        rules.TimeZoneId),
                    await BuildOfferAsync(localDate, rule.IdentityKey, rule.DisplayName, limit, usage, decision,
                        rule.Schedule, now, rules.TimeZoneId, cancellationToken)));
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
            var decision = RuleEvaluator.EvaluateApplication(appRule, now, rules.TimeZoneId, usage, rules.Language);
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
        var text = Text;
        _pendingApplicationLimitChanges.TryRemove(identityKey, out _);
        EnqueueCountdown(
            seconds => new UserNotification(
                text.ApplicationClosingTitle(displayName, seconds),
                text.SaveYourWorkNow(ShortReason(text, decision)),
                seconds,
                ApplicationWarningKey(identityKey),
                IsUrgent: true),
            graceSeconds);
        logger.LogWarning("Application {Application} is blocked for {Reason}; {GraceSeconds}-second save period started.",
            displayName, decision.Reason, graceSeconds);
    }

    public void DismissApplicationClosing(string identityKey) =>
        Enqueue(new UserNotification(
            string.Empty,
            string.Empty,
            PersistentNotificationKey: ApplicationWarningKey(identityKey),
            DismissPersistentNotification: true));

    public void NotifyPcSignOut(RuleDecision decision, int countdownSeconds)
    {
        var text = Text;
        EnqueueCountdown(
            seconds => new UserNotification(
                text.SignOutCountdownTitle(seconds),
                text.SaveYourWorkNow(ShortReason(text, decision)),
                seconds,
                "pc-sign-out",
                IsUrgent: true),
            countdownSeconds);
    }

    public void DismissPcSignOut() =>
        Enqueue(new UserNotification(
            string.Empty,
            string.Empty,
            PersistentNotificationKey: "pc-sign-out",
            DismissPersistentNotification: true));

    public void NotifyPcAvailable(RuleDecision previousDecision)
    {
        var text = Text;
        Enqueue(new UserNotification(
            text.PcAvailableTitle,
            text.PcAvailableMessage(ShortReason(text, previousDecision))));
    }

    /// <summary>Announces a completed background update. Informational, never urgent.</summary>
    public void NotifyServicesUpdated(string version)
    {
        var text = Text;
        Enqueue(new UserNotification(text.UpdatedTitle, text.UpdatedMessage(version)));
    }

    private void QueueRuleChangeNotifications(DeviceRuleSnapshot previous, DeviceRuleSnapshot current)
    {
        // The change is described with the language the new rules carry, so switching language and
        // limits in one save still announces itself in the language the parent just chose.
        var text = AgentStrings.For(current.Language);
        if (LimitsDiffer(previous.DailyLimitSeconds, previous.Schedule, current.DailyLimitSeconds, current.Schedule))
        {
            Enqueue(new UserNotification(
                text.PcLimitChangedTitle,
                BuildLimitChangeMessage(text, text.PcScopeName, previous.DailyLimitSeconds, previous.Schedule,
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
            text.ApplicationLimitChangedTitle(newApp.DisplayName),
            BuildLimitChangeMessage(text, newApp.DisplayName, oldApp.DailyLimitSeconds, oldApp.Schedule,
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
        AgentStrings text,
        string scope,
        int? oldDailyLimit,
        WeeklySchedule oldSchedule,
        int? newDailyLimit,
        WeeklySchedule newSchedule) =>
        text.LimitChangeMessage(
            scope,
            newDailyLimit,
            oldDailyLimit != newDailyLimit,
            !SchedulesEqual(oldSchedule, newSchedule));

    private static string ApplicationWarningKey(string identityKey) => $"application:{identityKey}";

    /// <summary>One key per restriction, so its reminders replace one another instead of piling up.</summary>
    private static string ReminderKey(string scope) => $"reminder:{scope}";

    private static string ShortReason(AgentStrings text, RuleDecision decision) => decision.Reason switch
    {
        BlockReason.ManualBlock => text.ShortReasonManualBlock,
        BlockReason.DailyLimitReached => text.ShortReasonDailyLimit,
        BlockReason.OutsideAllowedSchedule => text.ShortReasonOutsideSchedule,
        _ => text.ShortReasonUnavailable
    };

    private void QueueThresholdWarnings(string scope, string displayName, TimeRestriction restriction, bool signsOut)
    {
        var key = $"{scope}|{restriction.Key}";
        if (!_restrictionRemaining.TryGetValue(key, out var previous))
        {
            _restrictionRemaining[key] = restriction.RemainingSeconds;
            return;
        }

        var text = Text;
        foreach (var threshold in WarningThresholdSeconds)
        {
            if (previous <= threshold || restriction.RemainingSeconds > threshold) continue;
            // Urgent, because a reminder a child playing a game never notices is the same as no
            // reminder: the urgent scenario keeps the toast on screen and gets it past Focus
            // Assist. Still only a toast - no countdown, no card - because there is nothing to
            // act on yet. The key is this restriction's own, so 5 minutes replaces 15 rather
            // than stacking a second banner that stays until somebody dismisses it.
            Enqueue(new UserNotification(
                signsOut
                    ? text.PcTimeLeftTitle(threshold)
                    : text.ApplicationTimeLeftTitle(displayName, threshold),
                signsOut
                    ? text.PcTimeLeftMessage
                    : text.ApplicationTimeLeftMessage(displayName),
                PersistentNotificationKey: ReminderKey(scope),
                IsUrgent: true));
            logger.LogInformation("{Scope} restriction warning queued with {Seconds} seconds remaining.",
                displayName, threshold);
        }

        _restrictionRemaining[key] = restriction.RemainingSeconds;
    }

    private static UserNotification BuildApplicationOpenedNotification(
        AgentStrings text,
        string displayName,
        TimeRestriction restriction)
    {
        var message = (restriction.ActiveSecondsRemaining, restriction.ScheduleEndUtc) switch
        {
            (int remaining, { } scheduleEnd) =>
                text.ApplicationRemainingUntil(text.DurationWords(remaining), text.Deadline(scheduleEnd)),
            (int remaining, null) => text.ApplicationRemainingToday(text.DurationWords(remaining)),
            (null, { } scheduleEnd) => text.ApplicationAvailableUntil(text.Deadline(scheduleEnd)),
            _ => text.ApplicationTimeLimited
        };
        return new UserNotification(text.ApplicationTimeTitle(displayName), message);
    }

    private static TimeRestriction? BuildRestriction(
        int? dailyLimitSeconds,
        int activeSeconds,
        DateTimeOffset? scheduleEnd,
        DateTimeOffset utcNow,
        DateOnly localDate)
    {
        int? activeRemaining = dailyLimitSeconds is int limit ? Math.Max(0, limit - activeSeconds) : null;
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
        TimeBonus? bonus,
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        DateOnly localDate,
        string timeZoneId)
    {
        var bonusSeconds = TimeBonus.SecondsOn(bonus, localDate);
        var effectiveLimit = RuleEvaluator.EffectiveDailyLimitSeconds(dailyLimitSeconds, bonus, localDate);
        var isWithinSchedule = RuleEvaluator.IsWithinSchedule(schedule, utcNow, timeZoneId);
        var scheduleEnd = isWithinSchedule
            ? RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, utcNow, timeZoneId)
            : null;
        return new TimeAllowanceStatus(
            decision.IsAllowed,
            decision.Reason,
            decision.Message,
            activeSeconds,
            effectiveLimit,
            effectiveLimit is int limit ? Math.Max(0, limit - activeSeconds) : null,
            schedule.IsConfigured,
            isWithinSchedule,
            isWithinSchedule
                ? RuleEvaluator.FindCurrentAllowanceStartUtc(schedule, utcNow, timeZoneId)
                : null,
            scheduleEnd,
            !isWithinSchedule
                ? RuleEvaluator.FindNextAllowanceStartUtc(schedule, utcNow, timeZoneId)
                : null,
            bonusSeconds);
    }

    /// <summary>
    /// The child asking for more time, arriving from the tray agent over the pipe. The scope is
    /// resolved and its remaining seconds measured here - the one place that knows both the
    /// buffered usage and the limit actually in force - and the request itself is only recorded.
    /// Nothing on this path can grant anything: the extra minutes come back from the parent,
    /// inside the rules, like every other change.
    /// </summary>
    public async Task<TimeExtensionSubmissionResult> RequestTimeExtensionAsync(
        TimeExtensionSubmission submission,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var rules = _rules;
            var text = Text;
            var localDate = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), rules.TimeZoneId);

            var utcNow = clock.GetUtcNow();
            if (submission.ApplicationIdentityKey is not { Length: > 0 } identityKey)
            {
                var usage = await ReadUsageAsync(localDate, null, cancellationToken);
                var limit = RuleEvaluator.EffectiveDailyLimitSeconds(rules.DailyLimitSeconds, rules.Bonus, localDate);
                // A scope that is shut right now has no seconds left to measure, and that is the
                // one moment the question is worth asking, so it is passed as its own fact rather
                // than inferred from a remaining count of zero.
                var isBlocked = !RuleEvaluator.EvaluateDevice(rules, utcNow, usage).IsAllowed;
                return await extensions.SubmitAsync(localDate, null, text.PcScopeName, submission.Minutes,
                    RemainingOrNull(limit, usage),
                    isBlocked,
                    RuleEvaluator.GetAllowancePeriodKey(rules.Schedule, utcNow, rules.TimeZoneId),
                    text, cancellationToken);
            }

            if (rules.Applications.FirstOrDefault(item => item.IdentityKey == identityKey) is not { } appRule)
                return new TimeExtensionSubmissionResult(false, text.ExtraTimeNotPossible);
            var appUsage = await ReadUsageAsync(localDate, identityKey, cancellationToken);
            var appLimit = RuleEvaluator.EffectiveDailyLimitSeconds(appRule.DailyLimitSeconds, appRule.Bonus, localDate);
            var isAppBlocked = !RuleEvaluator
                .EvaluateApplication(appRule, utcNow, rules.TimeZoneId, appUsage, rules.Language).IsAllowed;
            return await extensions.SubmitAsync(localDate, identityKey, appRule.DisplayName, submission.Minutes,
                RemainingOrNull(appLimit, appUsage),
                isAppBlocked,
                RuleEvaluator.GetAllowancePeriodKey(appRule.Schedule, utcNow, rules.TimeZoneId),
                text, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Announces one answer the parent gave. Ordinary priority - nothing is closing.</summary>
    public void NotifyTimeExtensionDecision(LocalTimeExtension request)
    {
        var text = Text;
        Enqueue(request.Status == TimeExtensionStatus.Approved
            ? new UserNotification(
                text.ExtraTimeApprovedTitle,
                text.ExtraTimeApprovedMessage(request.DisplayName, request.GrantedMinutes))
            : new UserNotification(
                text.ExtraTimeDeniedTitle,
                text.ExtraTimeDeniedMessage(request.DisplayName)));
    }

    private static int? RemainingOrNull(int? limitSeconds, int activeSeconds) =>
        limitSeconds is int limit ? Math.Max(0, limit - activeSeconds) : null;

    /// <summary>
    /// The offer for one scope, or nothing when asking would be meaningless: an allowance with
    /// plenty of time still left, or none at all and nothing blocking it either.
    ///
    /// Anything that is actually shut - a spent daily limit, a manual block, a closed schedule
    /// window - can be asked about, because a grant now lifts all three: the minutes raise the
    /// limit, and a grant given while the scope was blocked outright runs from the moment the
    /// parent said yes. That a limit has already been reached is exactly the moment the sign-out
    /// card is on screen and the child has something to ask about. Caller holds the gate.
    /// </summary>
    private async Task<TimeExtensionOffer?> BuildOfferAsync(
        DateOnly localDate,
        string? identityKey,
        string displayName,
        int? limitSeconds,
        int activeSeconds,
        RuleDecision decision,
        WeeklySchedule schedule,
        DateTimeOffset utcNow,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        var remaining = RemainingOrNull(limitSeconds, activeSeconds);
        if (decision.IsAllowed
            && (remaining is not int left || left > TimeExtensionPolicy.RequestThresholdSeconds))
            return null;
        var periodKey = RuleEvaluator.GetAllowancePeriodKey(schedule, utcNow, timeZoneId);
        var (state, minutes) = await extensions.GetStateAsync(localDate, identityKey, periodKey, cancellationToken);
        return new TimeExtensionOffer(identityKey, displayName, state, minutes, remaining ?? 0);
    }

    private sealed record TimeRestriction(
        int RemainingSeconds,
        string Key,
        int? ActiveSecondsRemaining,
        DateTimeOffset? ScheduleEndUtc);
}
