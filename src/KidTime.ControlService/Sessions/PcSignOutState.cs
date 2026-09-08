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

    private long _issuedAtTimestamp;

    public void MarkIssued() => Volatile.Write(ref _issuedAtTimestamp, Stopwatch.GetTimestamp());

    public bool IsSigningOut
    {
        get
        {
            var issued = Volatile.Read(ref _issuedAtTimestamp);
            return issued != 0 && Stopwatch.GetElapsedTime(issued) < Window;
        }
    }
}
