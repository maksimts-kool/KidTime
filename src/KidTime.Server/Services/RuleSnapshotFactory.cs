using System.Text.Json;
using KidTime.Domain.Applications;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Services;

public sealed class RuleSnapshotFactory(KidTimeDbContext dbContext)
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
            Applications = appRules
                .Where(item => ApplicationCatalogPolicy.IsUserManageable(ToDescriptor(item)))
                .Select(item => new ApplicationRuleSnapshot
            {
                IdentityKey = item.Application.IdentityKey,
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)),
                ManuallyBlocked = item.Rule.ManuallyBlocked,
                DailyLimitSeconds = item.Rule.DailyLimitSeconds,
                Schedule = DeserializeSchedule(item.Rule.ScheduleJson),
                UpdatedAtUtc = item.Rule.UpdatedAtUtc
            }).ToList()
        };
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
