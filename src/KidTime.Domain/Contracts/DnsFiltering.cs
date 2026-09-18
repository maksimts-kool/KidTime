namespace KidTime.Domain.Contracts;

/// <summary>
/// What the household's DNS filter is doing right now, as one read-only picture.
///
/// KidTime does not filter anything itself and does not want to: the filtering is done by a
/// Technitium DNS server the parent already runs, and the parent manages it in its own console.
/// The KidTime server reads that configuration, normalizes it here, and passes it on so the
/// parent's panel can say whether it is on and the child's window can say what is in force and
/// until when. Nothing in this snapshot is enforced by the agent, and nothing in it is allowed to
/// reach <c>RuleEvaluator</c>.
///
/// It also carries only what is <em>configured</em> - never what was visited. Query logs are
/// deliberately not read: browsing history is on the far side of the privacy boundary the rest of
/// KidTime keeps, and a tab that listed the sites a child tried to open would cross it.
/// </summary>
public sealed class DnsFilteringSnapshot
{
    public static readonly DnsFilteringSnapshot NotConfigured =
        new() { State = DnsFilteringState.NotConfigured };

    public DnsFilteringState State { get; init; } = DnsFilteringState.NotConfigured;

    /// <summary>
    /// The filtering group this household falls in, as the DNS server names it ("kid"). It is
    /// shown to the parent and never to the child, for whom "your home network" is the whole of
    /// what the name means.
    /// </summary>
    public string? GroupName { get; init; }

    /// <summary>When the KidTime server last managed to read the DNS server. Null if it never has.</summary>
    public DateTimeOffset? RetrievedAtUtc { get; init; }

    /// <summary>
    /// The last read failed, so everything here is the answer before it. The state itself stays
    /// what it was: what a PC shows while it cannot check is the filtering that was in force, with
    /// its age attached, for the same reason cached rules keep being enforced offline. Hiding it
    /// would leave the child with an empty tab at exactly the moment they could not look it up.
    /// </summary>
    public bool IsStale { get; init; }

    /// <summary>What is blocked all of the time, grouped into things a person can name.</summary>
    public List<DnsFilterCategory> Categories { get; init; } = [];

    /// <summary>Named sets of sites - Roblox, Steam, Discord - that have hours of their own.</summary>
    public List<DnsSiteGroup> SiteGroups { get; init; } = [];
}

public enum DnsFilteringState
{
    /// <summary>No DNS server is configured on the KidTime server; the feature is simply absent.</summary>
    NotConfigured,

    /// <summary>
    /// Configured, but never read successfully - so there is no earlier answer to fall back on.
    /// A read that fails after an earlier success keeps that answer and sets
    /// <see cref="DnsFilteringSnapshot.IsStale"/> instead.
    /// </summary>
    Unreachable,

    /// <summary>Reachable, but this household is not in a filtering group, or blocking is off.</summary>
    Inactive,

    /// <summary>Filtering is on for this household.</summary>
    Active
}

/// <summary>
/// One kind of thing the filter blocks everywhere and always, and how many lists back it. The
/// child reads the kind; the count exists so a parent can tell one list from six.
/// </summary>
public sealed record DnsFilterCategory(DnsFilterCategoryKind Kind, int ListCount);

public enum DnsFilterCategoryKind
{
    Ads,
    Trackers,
    Adult,
    Gambling,
    Malware,
    Social,

    /// <summary>A list whose subject cannot be read from its address. Named rather than guessed.</summary>
    Other
}

/// <summary>
/// A named set of sites the parent can close and open on a timetable - the DNS half of what a
/// KidTime application rule does for a program. <paramref name="IsBlockedNow"/> is the answer for
/// the instant the snapshot was built; <paramref name="ChangesAtUtc"/> is when that answer next
/// turns over, so the child's window can say "back at 12:00" rather than only "blocked".
/// </summary>
public sealed record DnsSiteGroup(
    string Name,
    int SiteCount,
    bool IsBlockedNow,
    DateTimeOffset? ChangesAtUtc,
    List<DnsSiteWindow> Windows);

/// <summary>
/// One stretch of the week a site group is closed, in the wall-clock of the timezone the rule was
/// written in. <paramref name="Days"/> empty means every day; a window whose start is later than
/// its end runs over midnight.
/// </summary>
public sealed record DnsSiteWindow(string StartTime, string EndTime, List<DayOfWeek> Days);
