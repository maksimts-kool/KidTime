using System.Diagnostics;

namespace KidTime.ControlService.Enforcement;

/// <summary>
/// The save-work period granted to a blocked application before its processes are closed.
///
/// A lease covers one generation of processes and is retired the moment the close it authorizes
/// is issued. Holding it any longer - as an "already closed" flag on the identity - let an
/// application relaunched inside the same two-second sweep window inherit a spent lease: the
/// monitor then saw a lease for the current restriction episode whose close had already been
/// issued, and never closed the new process for the rest of that episode. Retiring the lease
/// also means a close that silently failed is retried on the next sweep instead of never.
/// </summary>
public sealed class ApplicationBlockLeases
{
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    /// <summary>True when nothing covers this application for the restriction episode it is in.</summary>
    public bool NeedsLease(string identityKey, string episodeKey) =>
        !_leases.TryGetValue(identityKey, out var lease)
        || !string.Equals(lease.EpisodeKey, episodeKey, StringComparison.Ordinal);

    /// <summary>Starts the save-work period the child was just warned about.</summary>
    public void Start(string identityKey, string episodeKey, int graceSeconds, long nowTimestamp) =>
        _leases[identityKey] = new Lease(
            episodeKey,
            nowTimestamp + (long)(graceSeconds * (double)Stopwatch.Frequency));

    /// <summary>
    /// Reports whether the save-work period has run out, retiring the lease when it has so the
    /// close it authorizes covers only the processes running at that moment.
    /// </summary>
    public bool TryTakeExpired(string identityKey, long nowTimestamp)
    {
        if (!_leases.TryGetValue(identityKey, out var lease) || nowTimestamp < lease.CloseAtTimestamp)
            return false;
        _leases.Remove(identityKey);
        return true;
    }

    /// <summary>Drops the lease, reporting whether one was held.</summary>
    public bool Release(string identityKey) => _leases.Remove(identityKey);

    /// <summary>Applications that still hold a lease although nothing of theirs is running.</summary>
    public IReadOnlyList<string> IdentitiesNoLongerRunning(ICollection<string> running) =>
        _leases.Keys.Where(identity => !running.Contains(identity)).ToList();

    public void Clear() => _leases.Clear();

    private sealed record Lease(string EpisodeKey, long CloseAtTimestamp);
}
