using System.Diagnostics;

namespace KidTime.ControlService.Sessions;

/// <summary>
/// Whether a Windows sign-out this service asked for is still taking effect.
///
/// <c>WTSLogoffSession</c> does not wait, and the session keeps existing for several seconds
/// afterwards with everything in it being torn down - so a SessionAgent launched into it is
/// killed before it can draw anything. Without this the supervisor reads those deaths as a crash
/// loop and reports a fault to the parent for a PC that is doing exactly what it was told.
/// </summary>
public sealed class PcSignOutState
{
    /// <summary>
    /// How long a sign-out is treated as still in progress. Deliberately shorter than
    /// <see cref="PcSignOutSchedule.SettlePeriod"/>: a sign-out that never takes effect is already
    /// reported as an error, and the child should get their tray agent back rather than lose it
    /// for as long as the failure lasts.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long Windows' own word that the session is being torn down stays good. The supervisor
    /// stands down for a minute after hearing it and then tries once more, which is what renews
    /// it, so this covers that minute and the launch after it.
    /// </summary>
    public static readonly TimeSpan TeardownEvidenceWindow = TimeSpan.FromSeconds(90);

    private long _issuedAtTimestamp;
    private long _teardownObservedTimestamp;

    public void MarkIssued() => MarkIssued(Stopwatch.GetTimestamp());

    public void MarkIssued(long nowTimestamp) => Volatile.Write(ref _issuedAtTimestamp, nowTimestamp);

    public bool IsSigningOut
    {
        get
        {
            var issued = Volatile.Read(ref _issuedAtTimestamp);
            return issued != 0 && Stopwatch.GetElapsedTime(issued) < Window;
        }
    }

    /// <summary>
    /// Records that Windows refused to start the agent because the session's window station is
    /// shutting down - proof that a sign-out is in progress, however long it is taking.
    /// </summary>
    public void NoteTeardownObserved() => NoteTeardownObserved(Stopwatch.GetTimestamp());

    public void NoteTeardownObserved(long nowTimestamp) => Volatile.Write(ref _teardownObservedTimestamp, nowTimestamp);

    /// <summary>
    /// True when Windows has said, since the last sign-out was issued and recently enough, that
    /// the session is still being torn down. A slow PC can take well over
    /// <see cref="PcSignOutSchedule.SettlePeriod"/> to sign a session out - a game saving, a
    /// profile unloading from a spinning disk - and that session has not outlived its sign-out;
    /// it is in the middle of it.
    /// </summary>
    public bool IsTearingDown(long nowTimestamp)
    {
        var issued = Volatile.Read(ref _issuedAtTimestamp);
        var observed = Volatile.Read(ref _teardownObservedTimestamp);
        return issued != 0
               && observed >= issued
               && Stopwatch.GetElapsedTime(observed, nowTimestamp) < TeardownEvidenceWindow;
    }
}
