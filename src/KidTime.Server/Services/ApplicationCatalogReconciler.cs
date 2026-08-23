using KidTime.Domain.Applications;
using KidTime.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Services;

public sealed class ApplicationCatalogReconciler(
    KidTimeDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ApplicationCatalogReconciler> logger)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var deviceApplications = await dbContext.DeviceApplications
            .Include(item => item.Application)
            .Include(item => item.Rule)
            .ToListAsync(cancellationToken);
        var changedDevices = new HashSet<Guid>();
        var groups = deviceApplications
            .Where(item => ApplicationCatalogPolicy.IsUserManageable(ToDescriptor(item)))
            .GroupBy(item => new
            {
                item.DeviceId,
                IdentityKey = ApplicationIdentity.CreateKey(
                    ApplicationCatalogPolicy.NormalizeForCatalog(ToDescriptor(item)))
            })
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var entries = group.ToList();
            var primary = entries
                .Where(item => string.Equals(item.Application.IdentityKey, group.Key.IdentityKey, StringComparison.Ordinal))
                .OrderByDescending(item => item.LastSeenUtc)
                .FirstOrDefault()
                ?? entries.OrderByDescending(item => item.LastSeenUtc).First();
            var duplicates = entries.Where(item => item.Id != primary.Id).ToList();

            var canonicalApplication = await dbContext.Applications
                .SingleOrDefaultAsync(item => item.IdentityKey == group.Key.IdentityKey, cancellationToken);
            if (canonicalApplication is null)
                primary.Application.IdentityKey = group.Key.IdentityKey;
            else if (primary.ApplicationId != canonicalApplication.Id)
            {
                primary.Application = canonicalApplication;
                primary.ApplicationId = canonicalApplication.Id;
            }

            // A duplicate created by later discovery receives a newer default rule timestamp.
            // Prefer an actually configured rule so a new observation cannot hide an existing limit.
            var selectedRule = entries
                .Where(item => IsConfigured(item.Rule))
                .OrderByDescending(item => item.Rule.UpdatedAtUtc)
                .Select(item => item.Rule)
                .FirstOrDefault()
                ?? entries.OrderByDescending(item => item.Rule.UpdatedAtUtc).First().Rule;
            primary.Rule.ManuallyBlocked = selectedRule.ManuallyBlocked;
            primary.Rule.DailyLimitSeconds = selectedRule.DailyLimitSeconds;
            primary.Rule.ScheduleJson = selectedRule.ScheduleJson;
            primary.Rule.UpdatedAtUtc = selectedRule.UpdatedAtUtc;
            primary.FirstSeenUtc = entries.Min(item => item.FirstSeenUtc);
            primary.LastSeenUtc = entries.Max(item => item.LastSeenUtc);
            primary.IconPng ??= entries.Select(item => item.IconPng).FirstOrDefault(icon => icon is { Length: > 0 });

            var ids = entries.Select(item => item.Id).ToList();
            var usage = await dbContext.DailyApplicationUsages
                .Where(item => ids.Contains(item.DeviceApplicationId))
                .ToListAsync(cancellationToken);
            foreach (var dateGroup in usage.GroupBy(item => item.LocalDate))
            {
                var primaryUsage = dateGroup.FirstOrDefault(item => item.DeviceApplicationId == primary.Id);
                if (primaryUsage is null)
                {
                    primaryUsage = new DailyApplicationUsage
                    {
                        DeviceApplicationId = primary.Id,
                        LocalDate = dateGroup.Key
                    };
                    dbContext.DailyApplicationUsages.Add(primaryUsage);
                }
                primaryUsage.ActiveSeconds = dateGroup.Sum(item => item.ActiveSeconds);
                primaryUsage.UpdatedAtUtc = dateGroup.Max(item => item.UpdatedAtUtc);
                dbContext.DailyApplicationUsages.RemoveRange(dateGroup.Where(item => item.Id != primaryUsage.Id));
            }

            dbContext.DeviceApplications.RemoveRange(duplicates);
            changedDevices.Add(group.Key.DeviceId);
            logger.LogInformation("Merged {DuplicateCount} duplicate catalog entries into {Application} ({IdentityKey}).",
                duplicates.Count, primary.Application.DisplayName, group.Key.IdentityKey);
        }

        foreach (var deviceId in changedDevices)
        {
            var rule = await dbContext.DeviceRules.SingleAsync(item => item.DeviceId == deviceId, cancellationToken);
            rule.Revision++;
            rule.UpdatedAtUtc = timeProvider.GetUtcNow();
            dbContext.DeviceCommands.Add(new DeviceCommand { DeviceId = deviceId, Type = "RulesChanged" });
        }

        if (changedDevices.Count > 0) await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsConfigured(ApplicationRule rule) =>
        rule.ManuallyBlocked
        || rule.DailyLimitSeconds is not null
        || RuleSnapshotFactory.DeserializeSchedule(rule.ScheduleJson).IsConfigured;

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
}
