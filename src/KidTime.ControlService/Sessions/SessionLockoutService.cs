using System.ComponentModel;
using System.Diagnostics;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Sessions;

public sealed class SessionLockoutService(
    EnforcementCoordinator coordinator,
    LocalStore store,
    ILogger<SessionLockoutService> logger) : BackgroundService
{
    internal const int FirstWarningSeconds = 60;
    internal const int RepeatWarningSeconds = 20;
    private readonly PcSignOutSchedule _signOut = new();
    private bool _wasBlocked;
    private bool _pendingAvailableNotification;
    private RuleDecision? _lastBlockedDecision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var status = await coordinator.GetPcStatusAsync(stoppingToken);
            var sessionId = WindowsSession.ActiveSessionId;
            var user = WindowsSession.GetActiveUserName();

            if (!WindowsSession.IsActiveUserControlled(coordinator.Rules))
            {
                if (_signOut.IsWarned) coordinator.DismissPcSignOut();
                _signOut.Clear();
                continue;
            }

            if (!status.Decision.IsAllowed)
            {
                _lastBlockedDecision = status.Decision;
                _wasBlocked = true;
                if (sessionId == uint.MaxValue || string.IsNullOrWhiteSpace(user))
                {
                    _signOut.Clear();
                    continue;
                }

                var step = _signOut.Next(sessionId, user, Stopwatch.GetTimestamp());
                if (step is SignOutStep.WarnAgain)
                {
                    // The parent has to learn that this PC did not actually sign out; the child
                    // is warned again rather than being signed out with no notice at all.
                    logger.LogError(
                        "Windows session {SessionId} ({User}) is still signed in after it was signed out; warning again.",
                        sessionId, user);
                }

                if (step is SignOutStep.Warn or SignOutStep.WarnAgain)
                    await WarnAndScheduleSignOutAsync(sessionId, user, status, stoppingToken);
                else if (step is SignOutStep.SignOut)
                {
                    _signOut.SignOutIssued(Stopwatch.GetTimestamp());
                    if (WindowsSession.TryLogoff(sessionId))
                        logger.LogWarning("Windows session {SessionId} ({User}) was signed out because the PC is blocked.", sessionId, user);
                    else
                        logger.LogError(new Win32Exception(), "Windows session {SessionId} ({User}) could not be signed out.", sessionId, user);
                }

                continue;
            }

            if (_wasBlocked)
            {
                _wasBlocked = false;
                _pendingAvailableNotification = true;
                coordinator.DismissPcSignOut();
                _signOut.Clear();
                logger.LogInformation("PC restriction ended; an availability notification is pending for the interactive user.");
            }

            if (_pendingAvailableNotification && !string.IsNullOrWhiteSpace(user) && _lastBlockedDecision is { } previous)
            {
                coordinator.NotifyPcAvailable(previous);
                _pendingAvailableNotification = false;
            }
        }
    }

    private async Task WarnAndScheduleSignOutAsync(
        uint sessionId,
        string user,
        PcEnforcementStatus status,
        CancellationToken cancellationToken)
    {
        var firstWarning = await store.TryConsumeFirstPcBlockGraceAsync(
            BuildEpisodeKey(status), cancellationToken);
        var warningSeconds = firstWarning ? FirstWarningSeconds : RepeatWarningSeconds;
        coordinator.NotifyPcSignOut(status.Decision, warningSeconds);
        logger.LogWarning("Persistent final warning queued for session {SessionId} ({User}); sign-out in {Seconds} seconds.",
            sessionId, user, warningSeconds);

        _signOut.Warn(sessionId, user, warningSeconds, Stopwatch.GetTimestamp());
    }

    private string BuildEpisodeKey(PcEnforcementStatus status) =>
        $"{coordinator.Rules.Revision}|{status.Decision.Reason}|{status.Decision.AvailableAtUtc?.UtcTicks}";
}
