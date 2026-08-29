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
        //
        // Yesterday's grants are read as well, and only for the window a grant given during a
        // block holds open: approved at ten to midnight for thirty minutes, it belongs to
        // yesterday's date and is still running.
        var now = timeProvider.GetUtcNow();
        var today = RuleEvaluator.GetLocalDate(now, device.TimeZoneId);
        var yesterday = today.AddDays(-1);
        var grants = await dbContext.TimeExtensions.AsNoTracking()
            .Where(item => item.DeviceId == deviceId
                           && (item.LocalDate == today || item.LocalDate == yesterday)
                           && item.Status == TimeExtensionStatuses.Approved
                           && item.GrantedMinutes > 0)
            .Select(item => new GrantRow(
                item.ApplicationIdentityKey, item.GrantedMinutes, item.LocalDate, item.DecidedAtUtc))
            .ToListAsync(cancellationToken);
        var pcGrants = grants.Where(grant => grant.ApplicationIdentityKey == null).ToList();
        var pcBonus = BuildBonus(
            today,
            pcGrants.Where(grant => grant.LocalDate == today).Sum(grant => grant.GrantedMinutes),
            LiftedUntil(pcGrants, now));
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
            ControlledUserSid = device.Rule.ControlledUserSid,
            ControlledUserName = device.Rule.ControlledUserName,
            IdleThresholdSeconds = device.Rule.IdleThresholdSeconds,
            ManuallyBlocked = device.Rule.ManuallyBlocked,
            ManualBlockUntilUtc = device.Rule.ManualBlockUntilUtc,
            DailyLimitSeconds = device.Rule.DailyLimitSeconds,
            Schedule = DeserializeSchedule(device.Rule.ScheduleJson),
            Bonus = pcBonus,
            Applications = appRules
                .Where(item => ApplicationCatalogPolicy.IsUserManageable(ToDescriptor(item)))
                .Select(item => new ApplicationRuleSnapshot
            {
                IdentityKey = item.Application.IdentityKey,
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)),
                ManuallyBlocked = item.Rule.ManuallyBlocked,
                DailyLimitSeconds = item.Rule.DailyLimitSeconds,
                Schedule = DeserializeSchedule(item.Rule.ScheduleJson),
                Bonus = BuildApplicationBonus(today, now, applicationGrants, item.Application.IdentityKey),
                UpdatedAtUtc = item.Rule.UpdatedAtUtc
            }).ToList()
        };
    }

    /// <summary>One approved grant, as much of it as the snapshot needs.</summary>
    private sealed record GrantRow(
        string? ApplicationIdentityKey,
        int GrantedMinutes,
        DateOnly LocalDate,
        DateTimeOffset? DecidedAtUtc);

    private static TimeBonus? BuildApplicationBonus(
        DateOnly today,
        DateTimeOffset now,
        Dictionary<string, List<GrantRow>> grantsByIdentity,
        string identityKey)
    {
        if (!grantsByIdentity.TryGetValue(identityKey, out var grants)) return null;
        return BuildBonus(
            today,
            grants.Where(grant => grant.LocalDate == today).Sum(grant => grant.GrantedMinutes),
            LiftedUntil(grants, now));
    }

    private static TimeBonus? BuildBonus(DateOnly localDate, int minutes, DateTimeOffset? liftedUntil) =>
        minutes > 0 || liftedUntil is not null ? new TimeBonus(localDate, minutes * 60, liftedUntil) : null;

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

    private static ApplicationDescriptor ToDescriptor(DeviceApplication item) => new()
    {
        DisplayName = item.Application.DisplayName,
        ExecutableName = item.Application.ExecutableName,
        ExecutablePath = item.ExecutablePath,
        ProductName = item.Application.ProductName,
        OriginalFilename = item.Application.OriginalFilename,
        Company = item.Application.Company,
        SignaturePublisher = item.Application.SignaturePublisher,
        FileVersion = item.FileVersion,
        PackageFamilyName = item.Application.PackageFamilyName,
        Sha256 = item.Sha256
    };

    public static string SerializeSchedule(WeeklySchedule schedule) =>
        JsonSerializer.Serialize(schedule, JsonOptions);

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
