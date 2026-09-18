using KidTime.Domain.Contracts;

namespace KidTime.Server.Services.Dns;

/// <summary>
/// Keeps one picture of the household's DNS filtering and hands it to whoever asks - the agent
/// sync of every enrolled PC, and the parent's panel.
///
/// It is a cache on purpose. The configuration changes when a parent changes it, so asking the
/// DNS server once every couple of minutes is generous; asking it once per PC per sync would put
/// a third-party service on the path of every synchronization, which is the one thing
/// synchronization must not depend on. A read that fails keeps the last good answer and says how
/// old it is, for the same reason cached rules keep being enforced offline.
/// </summary>
public sealed class DnsFilteringService(
    DnsFilteringOptions options,
    ILoggerFactory loggerFactory,
    TimeProvider timeProvider,
    ILogger<DnsFilteringService> logger) : IDisposable
{
    // Built here rather than registered, because "there is no DNS server" is an ordinary
    // configuration and a container holding a null service is not.
    private readonly TechnitiumCompanionClient? _client = options.IsConfigured
        ? new TechnitiumCompanionClient(options, loggerFactory.CreateLogger<TechnitiumCompanionClient>())
        : null;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DnsFilteringSnapshot _snapshot = DnsFilteringSnapshot.NotConfigured;
    private DateTimeOffset _nextRead = DateTimeOffset.MinValue;

    public string? ConsoleUrl => options.ResolveConsoleUrl();

    public async Task<DnsFilteringSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return DnsFilteringSnapshot.NotConfigured;
        var now = timeProvider.GetUtcNow();
        if (now < _nextRead) return _snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (now < _nextRead) return _snapshot;
            _nextRead = now.AddSeconds(Math.Clamp(options.RefreshSeconds, 15, 3_600));
            _snapshot = Build(await _client.ReadAsync(cancellationToken), now);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(exception, "The DNS filtering configuration could not be read.");
            // A failure after an earlier success keeps that answer whole - its state, its contents
            // and its timestamp - and only marks it stale, so the panel and the child's window go
            // on describing the filtering that is in force rather than going blank. With nothing
            // to fall back on there is nothing to describe, and the state says so.
            _snapshot = _snapshot.RetrievedAtUtc is null
                ? new DnsFilteringSnapshot { State = DnsFilteringState.Unreachable }
                : new DnsFilteringSnapshot
                {
                    State = _snapshot.State,
                    GroupName = _snapshot.GroupName,
                    RetrievedAtUtc = _snapshot.RetrievedAtUtc,
                    IsStale = true,
                    Categories = _snapshot.Categories,
                    SiteGroups = _snapshot.SiteGroups
                };
            // A failure is retried sooner than a success is refreshed, but not on every sync.
            _nextRead = now.AddSeconds(30);
        }
        finally
        {
            _gate.Release();
        }

        return _snapshot;
    }

    private DnsFilteringSnapshot Build(TechnitiumCompanionClient.CompanionState state, DateTimeOffset now)
    {
        var group = ResolveGroup(state);
        if (state.Blocking is not { EnableBlocking: true } || group is null || !group.EnableBlocking)
        {
            return new DnsFilteringSnapshot
            {
                State = DnsFilteringState.Inactive,
                GroupName = group?.Name,
                RetrievedAtUtc = now
            };
        }

        return new DnsFilteringSnapshot
        {
            State = DnsFilteringState.Active,
            GroupName = group.Name,
            RetrievedAtUtc = now,
            Categories = DnsFilterPolicy.Summarize(
            [
                .. group.BlockListUrls ?? [],
                .. group.AdblockListUrls ?? [],
                .. group.RegexBlockListUrls ?? []
            ]),
            SiteGroups = BuildSiteGroups(state, group.Name!, now)
        };
    }

    /// <summary>
    /// Which filtering group this household's PCs are in. The companion maps groups to networks,
    /// but the address the KidTime server sees is whatever the last proxy hop presents, so the
    /// group is named in configuration instead. A home with one group needs to name nothing.
    /// </summary>
    private TechnitiumCompanionClient.AdvancedBlockingGroup? ResolveGroup(
        TechnitiumCompanionClient.CompanionState state)
    {
        var groups = (state.Blocking?.Groups ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .ToList();
        if (groups.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(options.GroupName))
        {
            if (groups.Count == 1) return groups[0];
            logger.LogWarning(
                "The DNS server has {Count} filtering groups and Dns:GroupName names none of them.",
                groups.Count);
            return null;
        }

        return groups.FirstOrDefault(item =>
            string.Equals(item.Name, options.GroupName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The named sets of sites that have hours of their own, and where each one stands now. Only
    /// sets that actually restrict something are listed - a group the parent defined and never
    /// applied restricts nothing, and reading it as a rule would be a lie the child acts on.
    /// </summary>
    private List<DnsSiteGroup> BuildSiteGroups(
        TechnitiumCompanionClient.CompanionState state,
        string groupName,
        DateTimeOffset now)
    {
        var sizes = state.DomainGroups
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.First().Entries?.Count ?? 0, StringComparer.OrdinalIgnoreCase);

        // A set bound straight to the group is blocked with no timetable at all, which is the
        // shape of "this is off for good" rather than "this is off in the evenings".
        var results = new List<DnsSiteGroup>();
        foreach (var detail in state.DomainGroups.Where(item => !string.IsNullOrWhiteSpace(item.Name)))
        {
            var bound = (detail.Bindings ?? []).Any(binding =>
                string.Equals(binding.AdvancedBlockingGroupName, groupName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.Action, "block", StringComparison.OrdinalIgnoreCase));
            if (bound) results.Add(new DnsSiteGroup(detail.Name!, detail.Entries?.Count ?? 0, true, null, []));
        }

        foreach (var bucket in CollectScheduledSets(state, groupName))
        {
            if (results.Any(existing =>
                    string.Equals(existing.Name, bucket.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var (blockedNow, changesAt) = DnsFilterPolicy.Evaluate(bucket.Windows, bucket.TimeZone, now);
            results.Add(new DnsSiteGroup(
                bucket.Name,
                sizes.TryGetValue(bucket.Name, out var size) ? size : bucket.DirectSiteCount,
                blockedNow,
                changesAt,
                bucket.Windows));
        }

        return results
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Folds every enabled blocking schedule that targets this group onto the set of sites it is
    /// about. A schedule that names no advanced-blocking group applies to all of them, which is
    /// how the companion reads it too.
    /// </summary>
    private static IEnumerable<ScheduledSet> CollectScheduledSets(
        TechnitiumCompanionClient.CompanionState state,
        string groupName)
    {
        var buckets = new Dictionary<string, ScheduledSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in state.Schedules)
        {
            if (!rule.Enabled || !string.Equals(rule.Action, "block", StringComparison.OrdinalIgnoreCase))
                continue;
            var targets = rule.AdvancedBlockingGroupNames ?? [];
            if (targets.Count > 0
                && !targets.Any(item => string.Equals(item, groupName, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (rule.StartTime is not { Length: > 0 } || rule.EndTime is not { Length: > 0 }) continue;

            var window = new DnsSiteWindow(
                rule.StartTime,
                rule.EndTime,
                (rule.DaysOfWeek ?? [])
                    .Where(day => day is >= 0 and <= 6)
                    .Select(day => (DayOfWeek)day)
                    .Distinct()
                    .ToList());
            var names = (rule.DomainGroupNames ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).ToList();
            // A schedule can list domains itself instead of naming a set. Then the rule's own name
            // is what the child is shown, because it is the only name anybody gave those sites.
            if (names.Count == 0 && rule.DomainEntries is { Count: > 0 } && rule.Name is { Length: > 0 })
                names.Add(rule.Name);

            foreach (var name in names)
            {
                if (!buckets.TryGetValue(name, out var bucket))
                {
                    // Rules for one set share a timezone in every setup that makes sense; the
                    // first one seen decides, rather than the windows being evaluated against
                    // several clocks and the answers stitched together.
                    bucket = new ScheduledSet(name, ResolveTimeZone(rule.Timezone));
                    buckets[name] = bucket;
                }

                bucket.Windows.Add(window);
                bucket.DirectSiteCount = Math.Max(bucket.DirectSiteCount, rule.DomainEntries?.Count ?? 0);
            }
        }

        return buckets.Values;
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private sealed class ScheduledSet(string name, TimeZoneInfo timeZone)
    {
        public string Name { get; } = name;
        public TimeZoneInfo TimeZone { get; } = timeZone;
        public List<DnsSiteWindow> Windows { get; } = [];
        public int DirectSiteCount { get; set; }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _gate.Dispose();
    }
}
