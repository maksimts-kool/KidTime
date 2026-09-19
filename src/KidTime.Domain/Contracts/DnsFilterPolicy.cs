namespace KidTime.Domain.Contracts;

/// <summary>
/// Turns a DNS server's configuration into the two things a person can read: what is blocked all
/// the time, and when a named set of sites is closed.
///
/// It lives in the domain rather than in the server so both ends agree and so it can be tested
/// without a DNS server. Nothing here decides anything - it only describes.
/// </summary>
public static class DnsFilterPolicy
{
    /// <summary>
    /// The order categories are listed in. It runs from what a child is protected from towards
    /// what merely tidies the web up, so the first line of the card is the one worth reading.
    /// </summary>
    private static readonly DnsFilterCategoryKind[] DisplayOrder =
    [
        DnsFilterCategoryKind.Malware,
        DnsFilterCategoryKind.Adult,
        DnsFilterCategoryKind.Gambling,
        DnsFilterCategoryKind.Social,
        DnsFilterCategoryKind.Ads,
        DnsFilterCategoryKind.Trackers,
        DnsFilterCategoryKind.Other
    ];

    /// <summary>
    /// File names that name a list outright. HaGeZi's lists are the reason this exists: the one
    /// that blocks malware and phishing is called <c>tif.txt</c>, which says nothing to a keyword
    /// and would otherwise be read from the <c>/adblock/</c> directory it is served from.
    /// </summary>
    private static readonly Dictionary<string, DnsFilterCategoryKind> KnownListNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tif"] = DnsFilterCategoryKind.Malware,      // HaGeZi Threat Intelligence Feeds
            ["fake"] = DnsFilterCategoryKind.Malware,
            ["spam"] = DnsFilterCategoryKind.Malware,
            ["nsfw"] = DnsFilterCategoryKind.Adult,
            ["native"] = DnsFilterCategoryKind.Trackers,  // HaGeZi native OS/vendor trackers
            ["pro"] = DnsFilterCategoryKind.Ads,
            ["light"] = DnsFilterCategoryKind.Ads,
            ["multi"] = DnsFilterCategoryKind.Ads,
            ["ultimate"] = DnsFilterCategoryKind.Ads,
            ["popupads"] = DnsFilterCategoryKind.Ads,
            ["hoster"] = DnsFilterCategoryKind.Ads,
            ["hosts"] = DnsFilterCategoryKind.Ads
        };

    /// <summary>
    /// Words inside a list's file name, and failing that inside the rest of its path, that say
    /// what it is for. The host is never searched: AdGuard serves every one of its lists from
    /// <c>adguardteam.github.io</c>, so a household's gambling list would read as an ad list for
    /// no reason other than who publishes it.
    /// </summary>
    private static readonly (string Token, DnsFilterCategoryKind Kind)[] UrlTokens =
    [
        ("nsfw", DnsFilterCategoryKind.Adult),
        ("porn", DnsFilterCategoryKind.Adult),
        ("adult", DnsFilterCategoryKind.Adult),
        ("gambling", DnsFilterCategoryKind.Gambling),
        ("betting", DnsFilterCategoryKind.Gambling),
        ("casino", DnsFilterCategoryKind.Gambling),
        ("phishing", DnsFilterCategoryKind.Malware),
        ("malware", DnsFilterCategoryKind.Malware),
        ("threat", DnsFilterCategoryKind.Malware),
        ("scam", DnsFilterCategoryKind.Malware),
        ("badware", DnsFilterCategoryKind.Malware),
        ("tracker", DnsFilterCategoryKind.Trackers),
        ("tracking", DnsFilterCategoryKind.Trackers),
        ("telemetry", DnsFilterCategoryKind.Trackers),
        ("spyware", DnsFilterCategoryKind.Trackers),
        ("social", DnsFilterCategoryKind.Social),
        ("adaway", DnsFilterCategoryKind.Ads),
        ("adguard", DnsFilterCategoryKind.Ads),
        ("easylist", DnsFilterCategoryKind.Ads),
        ("adblock", DnsFilterCategoryKind.Ads),
        ("ads", DnsFilterCategoryKind.Ads)
    ];

    /// <summary>
    /// AdGuard's hostlist registry publishes every list as <c>filter_47.txt</c>, so the address
    /// carries a number and not a subject - and it carries the same publisher's name whatever the
    /// list is about, which is why the number is read before any word is. These are the ones a
    /// household is likely to be using; any other number is honestly reported as
    /// <see cref="DnsFilterCategoryKind.Other"/> rather than guessed at.
    /// </summary>
    private static readonly Dictionary<int, DnsFilterCategoryKind> AdGuardRegistryLists = new()
    {
        [1] = DnsFilterCategoryKind.Ads,        // AdGuard DNS filter
        [2] = DnsFilterCategoryKind.Ads,        // AdAway Default Blocklist
        [3] = DnsFilterCategoryKind.Ads,        // Peter Lowe's Blocklist
        [4] = DnsFilterCategoryKind.Ads,        // Dan Pollock's List
        [47] = DnsFilterCategoryKind.Gambling,  // HaGeZi's Gambling Blocklist
        [48] = DnsFilterCategoryKind.Adult,     // HaGeZi's NSFW Blocklist
        [49] = DnsFilterCategoryKind.Malware,   // HaGeZi's Threat Intelligence Feeds
        [63] = DnsFilterCategoryKind.Trackers   // HaGeZi's Windows/Office Tracker Blocklist
    };

    /// <summary>
    /// Reads one block list's address as a subject, narrowest evidence first: the registry number
    /// if it has one, then the file name, then the rest of the path. An address that says nothing
    /// is reported as <see cref="DnsFilterCategoryKind.Other"/> - a wrong label on a child's
    /// screen is worse than a vague one.
    /// </summary>
    public static DnsFilterCategoryKind ClassifyList(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return DnsFilterCategoryKind.Other;
        var (path, fileName) = SplitAddress(url);
        if (TryReadAdGuardRegistryId(fileName, out var id))
        {
            return AdGuardRegistryLists.TryGetValue(id, out var registryKind)
                ? registryKind
                : DnsFilterCategoryKind.Other;
        }

        // "tif.medium" is the same list as "tif", one size down, so the leading part counts too.
        if (KnownListNames.TryGetValue(fileName, out var named)) return named;
        var leading = fileName.Split('.')[0];
        if (KnownListNames.TryGetValue(leading, out named)) return named;

        foreach (var (token, kind) in UrlTokens)
            if (fileName.Contains(token, StringComparison.Ordinal)) return kind;
        foreach (var (token, kind) in UrlTokens)
            if (path.Contains(token, StringComparison.Ordinal)) return kind;
        return DnsFilterCategoryKind.Other;
    }

    /// <summary>
    /// The path and the bare file name, both lower case and with the host left out. An address
    /// that will not parse is treated as its own file name, which is the safe reading.
    /// </summary>
    private static (string Path, string FileName) SplitAddress(string url)
    {
        var path = url.Trim();
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)) path = absolute.AbsolutePath;
        path = path.ToLowerInvariant();
        var lastSlash = path.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (fileName.EndsWith(".txt", StringComparison.Ordinal)) fileName = fileName[..^4];
        return (path, fileName);
    }

    private static bool TryReadAdGuardRegistryId(string fileName, out int id)
    {
        id = 0;
        const string prefix = "filter_";
        return fileName.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(fileName[prefix.Length..], out id);
    }

    /// <summary>
    /// Folds every block list a group uses into the handful of categories a person can name,
    /// counting how many lists back each one. Duplicated addresses are counted once.
    /// </summary>
    public static List<DnsFilterCategory> Summarize(IEnumerable<string> blockListUrls)
    {
        var counts = new Dictionary<DnsFilterCategoryKind, int>();
        foreach (var url in blockListUrls
                     .Where(item => !string.IsNullOrWhiteSpace(item))
                     .Select(item => item.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var kind = ClassifyList(url);
            counts[kind] = counts.TryGetValue(kind, out var existing) ? existing + 1 : 1;
        }

        return DisplayOrder
            .Where(counts.ContainsKey)
            .Select(kind => new DnsFilterCategory(kind, counts[kind]))
            .ToList();
    }

    /// <summary>
    /// Works out whether a set of windows has a site group closed at <paramref name="nowUtc"/>,
    /// and when that answer next turns over. The times are wall-clock in
    /// <paramref name="timeZone"/>; a window whose end is not after its start runs over midnight,
    /// and an empty day list means every day.
    ///
    /// Overlapping windows are merged first, so two rules that meet at 18:00 read as one stretch
    /// ending at the later end rather than as a gap nobody would see.
    /// </summary>
    public static (bool IsBlockedNow, DateTimeOffset? ChangesAtUtc) Evaluate(
        IEnumerable<DnsSiteWindow> windows,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc)
    {
        var intervals = Merge(Expand(windows, timeZone, nowUtc));
        foreach (var (start, end) in intervals)
        {
            if (start > nowUtc) return (false, start);
            if (end > nowUtc) return (true, end);
        }

        return (false, null);
    }

    /// <summary>
    /// Turns recurring windows into the concrete instants around now. The span reaches a day back
    /// so an overnight window that started yesterday is still seen, and a full week forward so a
    /// group blocked only on Sundays can still say when it comes back.
    /// </summary>
    private static List<(DateTimeOffset Start, DateTimeOffset End)> Expand(
        IEnumerable<DnsSiteWindow> windows,
        TimeZoneInfo timeZone,
        DateTimeOffset nowUtc)
    {
        var results = new List<(DateTimeOffset, DateTimeOffset)>();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        foreach (var window in windows)
        {
            if (!TimeOnly.TryParse(window.StartTime, out var start)
                || !TimeOnly.TryParse(window.EndTime, out var end))
                continue;
            for (var offset = -1; offset <= 8; offset++)
            {
                var day = localNow.Date.AddDays(offset);
                if (window.Days.Count > 0 && !window.Days.Contains(day.DayOfWeek)) continue;
                var localStart = day.Add(start.ToTimeSpan());
                var localEnd = end > start
                    ? day.Add(end.ToTimeSpan())
                    : day.AddDays(1).Add(end.ToTimeSpan());
                results.Add((ToUtc(localStart, timeZone), ToUtc(localEnd, timeZone)));
            }
        }

        results.Sort((left, right) => left.Item1.CompareTo(right.Item1));
        return results;
    }

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(
        List<(DateTimeOffset Start, DateTimeOffset End)> sorted)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var interval in sorted)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
            {
                if (interval.End > merged[^1].End) merged[^1] = (merged[^1].Start, interval.End);
                continue;
            }

            merged.Add(interval);
        }

        return merged;
    }

    /// <summary>
    /// A wall-clock instant in the rule's own timezone. The hour a clock change skips does not
    /// exist and the hour it repeats happens twice, so both are resolved rather than thrown on:
    /// a daylight-saving boundary is not a reason to stop telling a child when their time ends.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(unspecified, timeZone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>
    /// What to tell a child whose page would not open, worked out from the filtering that is in
    /// force. It describes the rules and never the request: KidTime does not know which site was
    /// asked for and this deliberately does not try to guess, so the answer is the same sentence
    /// whatever the child typed.
    ///
    /// Null means there is nothing honest to say - filtering that is off, or a configuration that
    /// has never been read - and the child is then better served by silence than by KidTime
    /// blaming a rule it cannot see. A stale snapshot still answers: it is the filtering that was
    /// in force and almost certainly still is, which is the same reason cached rules go on being
    /// enforced offline.
    /// </summary>
    public static DnsFilterRefusal? ExplainRefusal(DnsFilteringSnapshot? snapshot, DateTimeOffset nowUtc)
    {
        if (snapshot is not { State: DnsFilteringState.Active }) return null;

        // Of several sets of sites that are shut, the one coming back soonest is the one worth
        // naming: it is the one the child can do something about, namely wait. A set with no
        // timetable at all comes back at no time and sorts behind every set that does.
        var closed = snapshot.SiteGroups
            .Where(group => group.IsBlockedNow && !string.IsNullOrWhiteSpace(group.Name))
            .OrderBy(group => group.ChangesAtUtc ?? DateTimeOffset.MaxValue)
            .FirstOrDefault();
        var categories = snapshot.Categories.Select(category => category.Kind).ToList();
        if (categories.Count == 0 && closed is null) return null;

        return new DnsFilterRefusal(
            categories,
            closed?.Name,
            closed?.ChangesAtUtc > nowUtc ? closed.ChangesAtUtc : null);
    }
}

/// <summary>
/// The reason a page did not open, as far as the configuration can account for it: the kinds of
/// site this household blocks at every hour, and - when one is shut right now - the named set of
/// sites that is, with the instant it comes back. <paramref name="ReopensAtUtc"/> is null for a
/// set that is simply off until a parent says otherwise.
/// </summary>
public sealed record DnsFilterRefusal(
    IReadOnlyList<DnsFilterCategoryKind> Categories,
    string? ClosedSiteGroup,
    DateTimeOffset? ReopensAtUtc);
