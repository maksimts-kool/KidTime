using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;

namespace KidTime.ControlService.Enforcement;

/// <summary>
/// The controlled PC's half of asking a parent for more time.
///
/// A request is only ever a question. Nothing here grants anything: an accepted request is
/// written to the local database, uploaded on the next synchronization, and the extra minutes
/// come back the ordinary way, inside the rule snapshot. That is deliberate - the tray agent is
/// unelevated and the child is the one at the keyboard, so the one thing they can reach must not
/// be a path to changing a rule.
///
/// The rules for what may be asked are checked here as well as on the server. This copy is not
/// the security boundary; it exists so a child gets a plain answer straight away rather than
/// discovering a minute later that nothing happened.
/// </summary>
public sealed class TimeExtensionService(LocalStore store, ILogger<TimeExtensionService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, LocalTimeExtension> _latestByScope = new(StringComparer.Ordinal);
    private DateOnly? _loadedDate;
    private int _requestsToday;

    /// <summary>
    /// Records one request, or explains why it was not recorded. <paramref name="remainingSeconds"/>
    /// is what the caller measured for this exact scope: null means the scope has no daily limit.
    /// <paramref name="isBlocked"/> says the scope is shut right now - a spent limit, a manual
    /// block, or a closed schedule window - and that settles it on its own: there is nothing left
    /// to measure, and a child looking at a block is exactly who this is for.
    /// </summary>
    public async Task<TimeExtensionSubmissionResult> SubmitAsync(
        DateOnly localDate,
        string? applicationIdentityKey,
        string displayName,
        int minutes,
        int? remainingSeconds,
        bool isBlocked,
        string periodKey,
        AgentStrings text,
        CancellationToken cancellationToken)
    {
        if (!TimeExtensionPolicy.IsAllowedRequest(minutes))
            return new TimeExtensionSubmissionResult(false, text.ExtraTimeNotPossible);
        if (!isBlocked)
        {
            // Nothing is shut, so there has to be an allowance running out to talk about.
            if (remainingSeconds is not int remaining)
                return new TimeExtensionSubmissionResult(false, text.ExtraTimeNotPossible);
            if (remaining > TimeExtensionPolicy.RequestThresholdSeconds)
                return new TimeExtensionSubmissionResult(false, text.ExtraTimeNotRunningOutYet);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(localDate, cancellationToken);
            var scope = applicationIdentityKey ?? string.Empty;
            if (_latestByScope.TryGetValue(scope, out var latest))
            {
                if (latest.Status == TimeExtensionStatus.Pending)
                    return new TimeExtensionSubmissionResult(false, text.ExtraTimeAlreadyAsked);
                // A refusal stands for the stretch of screen time it was given in. Asking again
                // five seconds later is how a request turns into pestering; asking again when the
                // next window opens is a fair question.
                if (latest.Status == TimeExtensionStatus.Denied
                    && string.Equals(latest.PeriodKey, periodKey, StringComparison.Ordinal))
                    return new TimeExtensionSubmissionResult(false, text.ExtraTimeDeniedUntilNextPeriod);
            }
            if (_requestsToday >= TimeExtensionPolicy.MaximumRequestsPerDay)
                return new TimeExtensionSubmissionResult(false, text.ExtraTimeTooManyToday);

            var request = new LocalTimeExtension(
                Guid.NewGuid(),
                localDate,
                applicationIdentityKey,
                displayName,
                minutes,
                0,
                TimeExtensionStatus.Pending,
                DateTimeOffset.UtcNow,
                periodKey);
            await store.AddTimeExtensionAsync(request, cancellationToken);
            _latestByScope[scope] = request;
            _requestsToday++;
            logger.LogInformation("Extra time requested: {Minutes} minutes for {Scope}.", minutes, displayName);
            return new TimeExtensionSubmissionResult(true, text.ExtraTimeSent);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// How the last request for one scope went, for the child's window.
    ///
    /// Two states stop them asking again: one still waiting on an answer, and a refusal given in
    /// the allowance period they are still in. A grant is only a caption - a child whose granted
    /// minutes have also run out may ask once more - and so is a refusal from an earlier period,
    /// which has expired along with the stretch of screen time it belonged to.
    /// </summary>
    public async Task<(TimeExtensionOfferState State, int Minutes)> GetStateAsync(
        DateOnly localDate,
        string? applicationIdentityKey,
        string periodKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await LoadAsync(localDate, cancellationToken);
            if (!_latestByScope.TryGetValue(applicationIdentityKey ?? string.Empty, out var latest))
                return (TimeExtensionOfferState.Available, 0);
            return latest.Status switch
            {
                TimeExtensionStatus.Pending => (TimeExtensionOfferState.Pending, latest.RequestedMinutes),
                TimeExtensionStatus.Approved => (TimeExtensionOfferState.Granted, latest.GrantedMinutes),
                // A refusal from an earlier period is history, not a lock: the child gets the
                // buttons back rather than a caption about something that is over.
                _ when !string.Equals(latest.PeriodKey, periodKey, StringComparison.Ordinal) =>
                    (TimeExtensionOfferState.Available, 0),
                _ => (TimeExtensionOfferState.Denied, 0)
            };
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<LocalTimeExtension>> GetPendingUploadsAsync(CancellationToken cancellationToken) =>
        store.GetTimeExtensionsToUploadAsync(cancellationToken);

    public Task MarkUploadedAsync(Guid requestId, CancellationToken cancellationToken) =>
        store.MarkTimeExtensionUploadedAsync(requestId, cancellationToken);

    /// <summary>
    /// Writes the parent's answers into the local record. Only requests still pending here change,
    /// so a server repeating a decision it already sent cannot announce the same answer twice.
    /// </summary>
    public async Task ApplyDecisionsAsync(
        IReadOnlyList<TimeExtensionDecision> decisions,
        CancellationToken cancellationToken)
    {
        if (decisions.Count == 0) return;
        foreach (var decision in decisions)
        {
            await store.ApplyTimeExtensionDecisionAsync(
                decision.RequestId,
                decision.Status,
                decision.Status == TimeExtensionStatus.Approved ? Math.Max(0, decision.GrantedMinutes) : 0,
                cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try { _loadedDate = null; }
        finally { _gate.Release(); }
    }

    /// <summary>Answers that have arrived and have not been shown to the child yet.</summary>
    public Task<IReadOnlyList<LocalTimeExtension>> GetAnnouncementsAsync(CancellationToken cancellationToken) =>
        store.GetTimeExtensionsToAnnounceAsync(cancellationToken);

    public Task MarkAnnouncedAsync(Guid requestId, CancellationToken cancellationToken) =>
        store.MarkTimeExtensionAnnouncedAsync(requestId, cancellationToken);

    /// <summary>Requests old enough that nobody is waiting on them stop being kept.</summary>
    public Task TrimAsync(DateOnly localDate, CancellationToken cancellationToken) =>
        store.TrimTimeExtensionsAsync(localDate.AddDays(-7), cancellationToken);

    /// <summary>Caller holds the gate.</summary>
    private async Task LoadAsync(DateOnly localDate, CancellationToken cancellationToken)
    {
        if (_loadedDate == localDate) return;
        var stored = await store.GetTimeExtensionsAsync(localDate, cancellationToken);
        _latestByScope.Clear();
        foreach (var item in stored) _latestByScope[item.ScopeKey] = item;
        _requestsToday = stored.Count;
        _loadedDate = localDate;
    }
}
