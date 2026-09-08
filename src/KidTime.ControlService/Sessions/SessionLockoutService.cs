using System.ComponentModel;
using System.Diagnostics;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Sessions;

public sealed class SessionLockoutService(
    EnforcementCoordinator coordinator,
    LocalStore store,
    PcSignOutState signOutState,
    ILogger<SessionLockoutService> logger) : BackgroundService
{
    internal const int FirstWarningSeconds = 60;
    internal const int RepeatWarningSeconds = 20;

    /// <summary>
    /// How far the deadline a standing warning was drawn for may move before the child is shown a
    /// new one. A warning issued ahead of a restriction counts down on the wall clock while the
    /// restriction itself may be reached sooner or later than predicted - a parent granting time,
    /// a schedule edit - and a card counting towards a sign-out that is no longer coming is worse
    /// than a second card.
    /// </summary>
    private const int ReWarnDriftSeconds = 10;

    private readonly PcSignOutSchedule _signOut = new();
    private bool _wasBlocked;
    private bool _pendingAvailableNotification;
    private RuleDecision? _lastBlockedDecision;

    /// <summary>The controlled session signed in at the previous tick, if any.</summary>
    private SignedInSession? _previousSession;

    /// <summary>True while the standing warning was issued before the restriction arrived.</summary>
    private bool _warnedAhead;

    private readonly record struct SignedInSession(uint SessionId, string User)
    {
        public bool Is(SignedInSession? other) =>
            other is { } value
            && value.SessionId == SessionId
            && string.Equals(value.User, User, StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var status = await coordinator.GetPcStatusAsync(stoppingToken);

            if (!WindowsSession.IsActiveUserControlled(coordinator.Rules))
            {
                ClearWarning();
                _previousSession = null;
                continue;
            }

            var sessionId = WindowsSession.ActiveSessionId;
            var user = WindowsSession.GetActiveUserName();
            SignedInSession? session = sessionId != uint.MaxValue && !string.IsNullOrWhiteSpace(user)
                ? new SignedInSession(sessionId, user)
                : null;

            if (!status.Decision.IsAllowed)
            {
                await HandleBlockedAsync(session, status, stoppingToken);
                _previousSession = session;
                continue;
            }

            if (_wasBlocked)
            {
                _wasBlocked = false;
                _pendingAvailableNotification = true;
                ClearWarning();
                logger.LogInformation("PC restriction ended; an availability notification is pending for the interactive user.");
            }

            WarnAheadOfRestriction(session, status);

            if (_pendingAvailableNotification && session is not null && _lastBlockedDecision is { } previous)
            {
                coordinator.NotifyPcAvailable(previous);
                _pendingAvailableNotification = false;
            }

            _previousSession = session;
        }
    }

    private async Task HandleBlockedAsync(
        SignedInSession? session,
        PcEnforcementStatus status,
        CancellationToken cancellationToken)
    {
        _lastBlockedDecision = status.Decision;
        _wasBlocked = true;
        if (session is not { } signedIn)
        {
            _signOut.Clear();
            _warnedAhead = false;
            return;
        }

        var step = _signOut.Next(signedIn.SessionId, signedIn.User, Stopwatch.GetTimestamp());
        if (step is SignOutStep.WarnAgain)
        {
            // The parent has to learn that this PC did not actually sign out; the child
            // is warned again rather than being signed out with no notice at all.
            logger.LogError(
                "Windows session {SessionId} ({User}) is still signed in after it was signed out; warning again.",
                signedIn.SessionId, signedIn.User);
        }

        if (step is SignOutStep.Warn or SignOutStep.WarnAgain)
        {
            // Whether the child was here to be warned decides how long they get. A restriction
            // that arrived while they were signed in - a parent locking the PC from the panel -
            // is worth the full minute even though nothing could be counted down to it. Finding
            // one already in force on the way in is worth the short one: there is no work in
            // progress to save, and the PC is meant to be shut.
            var wasPresent = signedIn.Is(_previousSession);
            var seconds = wasPresent
                          && await store.TryConsumeFirstPcBlockGraceAsync(
                              BuildEpisodeKey(status.Decision), cancellationToken)
                ? FirstWarningSeconds
                : RepeatWarningSeconds;
            WarnAndScheduleSignOut(signedIn, status.Decision, seconds);
        }
        else if (step is SignOutStep.SignOut)
        {
            _signOut.SignOutIssued(Stopwatch.GetTimestamp());
            _warnedAhead = false;
            signOutState.MarkIssued();
            if (WindowsSession.TryLogoff(signedIn.SessionId))
                logger.LogWarning("Windows session {SessionId} ({User}) was signed out because the PC is blocked.", signedIn.SessionId, signedIn.User);
            else
                logger.LogError(new Win32Exception(), "Windows session {SessionId} ({User}) could not be signed out.", signedIn.SessionId, signedIn.User);
        }
    }

    /// <summary>
    /// Starts the final warning so that it ends where the screen time does. A schedule closing at
    /// 22:00 warns at 21:59 and signs out on the hour, instead of warning on the hour and signing
    /// out a minute into time the child was not supposed to have.
    /// </summary>
    private void WarnAheadOfRestriction(SignedInSession? session, PcEnforcementStatus status)
    {
        if (session is not { } signedIn
            || status.Pending is not { } pending
            || pending.Seconds > FirstWarningSeconds)
        {
            // Nothing is closing any more - a parent granted time, or the child stopped short of
            // a limit that was about to run out. A card counting down to a sign-out that is not
            // coming is withdrawn rather than left to expire.
            if (_warnedAhead) ClearWarning();
            return;
        }

        var standing = _signOut.SecondsRemaining(Stopwatch.GetTimestamp());
        if (standing is not null && !_warnedAhead) return;
        if (standing is int left && Math.Abs(left - pending.Seconds) <= ReWarnDriftSeconds) return;
        if (standing is not null) coordinator.DismissPcSignOut();

        // The countdown is the time left, not a fixed grace: this warning has to end where the
        // screen time does. The persisted first-block grace is deliberately left unconsumed - it
        // is what a child who was not warned in advance gets, and this one was.
        WarnAndScheduleSignOut(signedIn, pending.Decision, Math.Max(1, pending.Seconds));
        _warnedAhead = true;
    }

    private void WarnAndScheduleSignOut(SignedInSession session, RuleDecision decision, int warningSeconds)
    {
        coordinator.NotifyPcSignOut(decision, warningSeconds);
        logger.LogWarning("Persistent final warning queued for session {SessionId} ({User}); sign-out in {Seconds} seconds.",
            session.SessionId, session.User, warningSeconds);

        _signOut.Warn(session.SessionId, session.User, warningSeconds, Stopwatch.GetTimestamp());
    }

    private void ClearWarning()
    {
        if (_signOut.IsWarned) coordinator.DismissPcSignOut();
        _signOut.Clear();
        _warnedAhead = false;
    }

    private string BuildEpisodeKey(RuleDecision decision) =>
        $"{coordinator.Rules.Revision}|{decision.Reason}|{decision.AvailableAtUtc?.UtcTicks}";
}
