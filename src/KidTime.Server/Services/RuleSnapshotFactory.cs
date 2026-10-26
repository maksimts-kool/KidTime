using System.Text.Json;
using KidTime.Domain.Applications;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Services;

public sealed class RuleSnapshotFactory(KidTimeDbContext dbContext, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DeviceRuleSnapshot> CreateAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await dbContext.Devices.AsNoTracking()
            .Include(x => x.Rule)
            .SingleAsync(x => x.Id == deviceId, cancellationToken);
        var appRules = await dbContext.DeviceApplications.AsNoTracking()
            .Where(x => x.DeviceId == deviceId)
            .Include(x => x.Application)
            .Include(x => x.Rule)
            .ToListAsync(cancellationToken);

        // Extra time a parent granted today travels as part of the rules, so it reaches the PC
        // over the path that already exists and stops applying by itself once the device-local
        // date moves on. A grant for any other date adds no minutes to today's limit.
        var now = timeProvider.GetUtcNow();
        var today = RuleEvaluator.GetLocalDate(now, device.TimeZoneId);
        var grants = await RecentGrants(dbContext.TimeExtensions.AsNoTracking().Where(item => item.DeviceId == deviceId), now)
            .ToListAsync(cancellationToken);
        var pcBonus = BonusFor(grants.Where(grant => grant.ApplicationIdentityKey == null), today, now);
        var periods = await ActivePeriods(dbContext.SpecialPeriods.AsNoTracking(), now)
            .Where(period => period.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        var applicationGrants = grants
            .Where(grant => grant.ApplicationIdentityKey != null)
            .GroupBy(grant => grant.ApplicationIdentityKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        return new DeviceRuleSnapshot
        {
            DeviceId = device.Id,
            Revision = device.Rule.Revision,
            TimeZoneId = device.TimeZoneId,
            Language = device.Rule.Language,
            OpenWindowAtSignIn = device.Rule.OpenWindowAtSignIn,
            ControlledUserSid = device.Rule.ControlledUserSid,
            ControlledUserName = device.Rule.ControlledUserName,
            IdleThresholdSeconds = device.Rule.IdleThresholdSeconds,
            ManuallyBlocked = device.Rule.ManuallyBlocked,
            ManualBlockUntilUtc = device.Rule.ManualBlockUntilUtc,
            DailyLimitSeconds = device.Rule.DailyLimitSeconds,
            Schedule = Compose(
                device.Rule.ScheduleJson,
                periods.Where(period => period.DeviceApplicationId == null)),
            Bonus = pcBonus,
            Applications = appRules
                .Where(item => ApplicationCatalogPolicy.IsUserManageable(item.ToDescriptor()))
                .Select(item => new ApplicationRuleSnapshot
            {
                IdentityKey = item.Application.IdentityKey,
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(item.ToDescriptor()),
                ManuallyBlocked = item.Rule.ManuallyBlocked,
                DailyLimitSeconds = item.Rule.DailyLimitSeconds,
                Schedule = Compose(
                    item.Rule.ScheduleJson,
                    periods.Where(period => period.DeviceApplicationId == item.Id)),
                Bonus = applicationGrants.TryGetValue(item.Application.IdentityKey, out var appGrants)
                    ? BonusFor(appGrants, today, now)
                    : null,
                UpdatedAtUtc = item.Rule.UpdatedAtUtc
            }).ToList()
        };
    }

    /// <summary>One approved grant, as much of it as a bonus needs.</summary>
    public sealed record GrantRow
    {
        public Guid DeviceId { get; init; }
        public string? ApplicationIdentityKey { get; init; }
        public int GrantedMinutes { get; init; }
        public DateOnly LocalDate { get; init; }
        public DateTimeOffset? DecidedAtUtc { get; init; }
    }

    /// <summary>
    /// Approved grants recent enough to matter now. Today's add minutes to today's limit;
    /// yesterday's are read only for the window a grant given during a block holds open - approved
    /// at ten to midnight for thirty minutes, it belongs to yesterday's date and is still running.
    /// The dates are the PC's own and can sit a day either side of UTC's, so this keeps two days
    /// back and leaves <see cref="BonusFor"/> to pick out what applies.
    /// </summary>
    public static IQueryable<GrantRow> RecentGrants(IQueryable<TimeExtension> source, DateTimeOffset now)
    {
        var earliest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2);
        return source
            .Where(item => item.LocalDate >= earliest
                           && item.Status == TimeExtensionStatuses.Approved
                           && item.GrantedMinutes > 0)
            .Select(item => new GrantRow
            {
                DeviceId = item.DeviceId,
                ApplicationIdentityKey = item.ApplicationIdentityKey,
                GrantedMinutes = item.GrantedMinutes,
                LocalDate = item.LocalDate,
                DecidedAtUtc = item.DecidedAtUtc
            });
    }

    /// <summary>
    /// What one scope's grants add up to on <paramref name="today"/>: the minutes granted for that
    /// date, and the window the latest of them holds a block or closed schedule open.
    /// </summary>
    public static TimeBonus? BonusFor(IEnumerable<GrantRow> grants, DateOnly today, DateTimeOffset now)
    {
        var yesterday = today.AddDays(-1);
        var recent = grants.Where(grant => grant.LocalDate == today || grant.LocalDate == yesterday).ToList();
        var minutes = recent.Where(grant => grant.LocalDate == today).Sum(grant => grant.GrantedMinutes);
        var liftedUntil = LiftedUntil(recent, now);
        return minutes > 0 || liftedUntil is not null ? new TimeBonus(today, minutes * 60, liftedUntil) : null;
    }

    /// <summary>
    /// How long a grant given while the scope was blocked outright keeps holding that block off:
    /// the granted minutes counted from the decision, which is the moment both the parent and the
    /// child watched it start. Two grants running at once do not add up - the later decision moves
    /// the end, it does not queue behind the earlier one.
    /// </summary>
    private static DateTimeOffset? LiftedUntil(IEnumerable<GrantRow> grants, DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        foreach (var grant in grants)
        {
            if (grant.DecidedAtUtc is not DateTimeOffset decidedAt) continue;
            var until = decidedAt.AddMinutes(grant.GrantedMinutes);
            if (until > now && (latest is null || until > latest)) latest = until;
        }

        return latest;
    }

    /// <summary>
    /// Special periods that have not ended yet. Their dates are the PC's own, and the PC's day can
    /// be a day behind UTC's, so this keeps everything that ended up to yesterday in UTC terms and
    /// leaves <see cref="ScheduleOverride.Covers"/> to say whether a date really falls inside.
    /// Upcoming periods travel too: a PC that is offline on the first morning of a holiday still
    /// switches over at its own midnight.
    /// </summary>
    public static IQueryable<SpecialPeriod> ActivePeriods(IQueryable<SpecialPeriod> periods, DateTimeOffset now)
    {
        var earliest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        return periods.Where(period => period.EndDate >= earliest);
    }

    /// <summary>A rule's stored weekly schedule with the special periods that apply to it.</summary>
    public static WeeklySchedule Compose(string? scheduleJson, IEnumerable<SpecialPeriod> periods) => new()
    {
        Days = DeserializeSchedule(scheduleJson).Days,
        Overrides = periods.OrderBy(period => period.StartDate).Select(ToOverride).ToList()
    };

    public static ScheduleOverride ToOverride(SpecialPeriod period) => new()
    {
        Name = period.Name,
        StartDate = period.StartDate,
        EndDate = period.EndDate,
        Days = DeserializeSchedule(period.ScheduleJson).Days,
        DailyLimitSeconds = period.DailyLimitSeconds
    };

    /// <summary>
    /// Special periods live in their own table, so a stored schedule never carries any: a parent
    /// saving the weekly schedule cannot overwrite or duplicate a holiday by sending it back.
    /// </summary>
    public static string SerializeSchedule(WeeklySchedule schedule) =>
        JsonSerializer.Serialize(schedule.WithoutOverrides(), JsonOptions);

    public static WeeklySchedule DeserializeSchedule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new WeeklySchedule();
        try
        {
            return JsonSerializer.Deserialize<WeeklySchedule>(json, JsonOptions) ?? new WeeklySchedule();
        }
        catch (JsonException)
        {
            return new WeeklySchedule();
        }
    }
}
