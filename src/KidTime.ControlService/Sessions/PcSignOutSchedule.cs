using System.Diagnostics;

namespace KidTime.ControlService.Sessions;

/// <summary>What the lockout loop owes the blocked session it is looking at.</summary>
public enum SignOutStep
{
    /// <summary>The warning is still counting down, or the sign-out is still taking effect.</summary>
    Wait,

    /// <summary>This session has not been warned yet.</summary>
    Warn,

    /// <summary>The session outlived its sign-out, so the cycle has to start over.</summary>
    WarnAgain,

    /// <summary>The warning has run out and the session has to be signed out now.</summary>
    SignOut
}

/// <summary>
/// The final warning granted to one signed-in session before it is signed out.
///
/// The schedule covers a single sign-out attempt and is spent the moment that sign-out is
/// issued. Latching "already signed out" onto the session instead would leave one that
/// outlived the call - a <c>WTSLogoffSession</c> that failed, or one that never took
/// effect - in use for the rest of the restriction, with the service declining to act again
/// and only an error in the log to show for it.
/// </summary>
public sealed class PcSignOutSchedule
{
    /// <summary>
    /// How long a sign-out is given to take effect before the warning cycle starts over. The
    /// call does not wait for the session to end, so a session still present for a few seconds
    /// afterwards is ordinary teardown rather than a failure.
    /// </summary>
    public static readonly TimeSpan SettlePeriod = TimeSpan.FromSeconds(30);

    private uint? _sessionId;
    private string? _user;
    private long _signOutAtTimestamp;
    private long? _issuedAtTimestamp;

    /// <summary>True while a warning is standing, so the loop knows there is one to withdraw.</summary>
    public bool IsWarned => _sessionId is not null;

    /// <summary>
    /// Seconds left on a standing warning, or null when none is counting down. A warning issued
    /// before the restriction arrived is measured against this to notice that the deadline it was
    /// drawn for has moved - a parent granting time, or a child stopping short of a limit.
    /// </summary>
    public int? SecondsRemaining(long nowTimestamp) =>
        _sessionId is null || _issuedAtTimestamp is not null
            ? null
            : (int)Math.Ceiling(Math.Max(0, Stopwatch.GetElapsedTime(nowTimestamp, _signOutAtTimestamp).TotalSeconds));

    public SignOutStep Next(uint sessionId, string user, long nowTimestamp)
    {
        if (_sessionId != sessionId || !string.Equals(_user, user, StringComparison.OrdinalIgnoreCase))
            return SignOutStep.Warn;
        if (_issuedAtTimestamp is { } issued)
            return Stopwatch.GetElapsedTime(issued, nowTimestamp) >= SettlePeriod
                ? SignOutStep.WarnAgain
                : SignOutStep.Wait;
        return nowTimestamp >= _signOutAtTimestamp ? SignOutStep.SignOut : SignOutStep.Wait;
    }

    /// <summary>Starts the warning the child was just shown.</summary>
    public void Warn(uint sessionId, string user, int warningSeconds, long nowTimestamp)
    {
        _sessionId = sessionId;
        _user = user;
        _issuedAtTimestamp = null;
        _signOutAtTimestamp = nowTimestamp + (long)(warningSeconds * (double)Stopwatch.Frequency);
    }

    /// <summary>Spends the warning on the sign-out it authorized.</summary>
    public void SignOutIssued(long nowTimestamp) => _issuedAtTimestamp = nowTimestamp;

    public void Clear()
    {
        _sessionId = null;
        _user = null;
        _signOutAtTimestamp = 0;
        _issuedAtTimestamp = null;
    }
}
