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
        // date moves on. A grant for any other date is simply not read.
        var today = RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), device.TimeZoneId);
        var grants = await dbContext.TimeExtensions.AsNoTracking()
            .Where(item => item.DeviceId == deviceId
                           && item.LocalDate == today
                           && item.Status == TimeExtensionStatuses.Approved
                           && item.GrantedMinutes > 0)
            .Select(item => new { item.ApplicationIdentityKey, item.GrantedMinutes })
            .ToListAsync(cancellationToken);
        var pcBonus = BuildBonus(today, grants
            .Where(grant => grant.ApplicationIdentityKey == null)
            .Sum(grant => grant.GrantedMinutes));
        var applicationBonuses = grants
            .Where(grant => grant.ApplicationIdentityKey != null)
            .GroupBy(grant => grant.ApplicationIdentityKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(grant => grant.GrantedMinutes), StringComparer.Ordinal);

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
                Bonus = BuildBonus(today, applicationBonuses.GetValueOrDefault(item.Application.IdentityKey)),
                UpdatedAtUtc = item.Rule.UpdatedAtUtc
            }).ToList()
        };
    }

    private static TimeBonus? BuildBonus(DateOnly localDate, int minutes) =>
        minutes > 0 ? new TimeBonus(localDate, minutes * 60) : null;

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
