using KidTime.Domain.Applications;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/applications")]
public sealed class ApplicationsController(
    KidTimeDbContext dbContext,
    RuleChangePublisher ruleChanges,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// The block switch, and the schedule only when one is sent; the panel edits the schedule and
    /// the daily limit in the schedule card, through PUT schedule and PUT limit.
    /// </summary>
    public sealed record UpdateApplicationRuleRequest(
        bool ManuallyBlocked,
        WeeklySchedule? Schedule = null);

    public sealed record UpdateLimitRequest(int? DailyLimitSeconds);

    [HttpGet]
    public async Task<IActionResult> List(Guid? deviceId, string? search, CancellationToken cancellationToken)
    {
        var query = dbContext.DeviceApplications.AsNoTracking();
        if (deviceId is not null) query = query.Where(x => x.DeviceId == deviceId);
        // Icons are left out deliberately: the list only says whether one exists, and the browser
        // fetches each one by itself - loading every icon's bytes here made the whole catalog
        // travel from the database on every page that lists applications.
        var loaded = await query
            .Select(x => new
            {
                Item = new DeviceApplication
                {
                    Id = x.Id,
                    DeviceId = x.DeviceId,
                    Device = x.Device,
                    ApplicationId = x.ApplicationId,
                    Application = x.Application,
                    ExecutablePath = x.ExecutablePath,
                    FileVersion = x.FileVersion,
                    Sha256 = x.Sha256,
                    FirstSeenUtc = x.FirstSeenUtc,
                    LastSeenUtc = x.LastSeenUtc,
                    Rule = x.Rule
                },
                HasIcon = x.IconPng != null && x.IconPng.Length > 0
            })
            .ToListAsync(cancellationToken);
        var withIcon = loaded.Where(row => row.HasIcon).Select(row => row.Item.Id).ToHashSet();
        var term = search?.Trim();
        // The descriptor and the friendly name are worked out once per row: filtering, grouping,
        // sorting and the response all read them.
        var applications = loaded
            .Select(row => (row.Item, Descriptor: row.Item.ToDescriptor()))
            .Where(row => ApplicationCatalogPolicy.IsUserManageable(row.Descriptor))
            .Select(row => (row.Item, row.Descriptor, Name: ApplicationCatalogPolicy.GetFriendlyDisplayName(row.Descriptor)))
            .Where(row => string.IsNullOrEmpty(term)
                          || row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || row.Item.Application.ExecutableName.Contains(term, StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => (row.Item.DeviceId, Name: row.Name.ToUpperInvariant()))
            .Select(group => group.MaxBy(row => row.Item.LastSeenUtc))
            .OrderBy(row => row.Name)
            .ToList();
        var result = new List<object>(applications.Count);
        var now = timeProvider.GetUtcNow();
        var periods = (await RuleSnapshotFactory.ActivePeriods(dbContext.SpecialPeriods.AsNoTracking(), now)
                .Where(period => period.DeviceApplicationId != null)
                .ToListAsync(cancellationToken))
            .ToLookup(period => period.DeviceApplicationId);
        var grants = (await RuleSnapshotFactory.RecentGrants(dbContext.TimeExtensions.AsNoTracking(), now)
                .Where(grant => grant.ApplicationIdentityKey != null)
                .ToListAsync(cancellationToken))
            .ToLookup(grant => (grant.DeviceId, grant.ApplicationIdentityKey));
        var localDates = applications
            .Select(row => row.Item.Device).DistinctBy(device => device.Id)
            .ToDictionary(device => device.Id, device => RuleEvaluator.GetLocalDate(now, device.TimeZoneId));
        var earliestDate = localDates.Values.DefaultIfEmpty().Min();
        var applicationIds = applications.Select(row => row.Item.Id).ToList();
        var usage = (await dbContext.DailyApplicationUsages.AsNoTracking()
                .Where(x => applicationIds.Contains(x.DeviceApplicationId) && x.LocalDate >= earliestDate)
                .Select(x => new { x.DeviceApplicationId, x.LocalDate, x.ActiveSeconds })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => (x.DeviceApplicationId, x.LocalDate), x => x.ActiveSeconds);
        foreach (var (item, descriptor, displayName) in applications)
        {
            var today = localDates[item.DeviceId];
            var schedule = RuleSnapshotFactory.Compose(item.Rule.ScheduleJson, periods[item.Id]);
            var bonus = RuleSnapshotFactory.BonusFor(grants[(item.DeviceId, item.Application.IdentityKey)], today, now);
            result.Add(new
            {
                id = item.Id,
                deviceId = item.DeviceId,
                deviceName = item.Device.Name,
                identityKey = item.Application.IdentityKey,
                displayName,
                executableName = item.Application.ExecutableName,
                item.ExecutablePath,
                item.FileVersion,
                publisher = item.Application.SignaturePublisher ?? item.Application.Company,
                item.FirstSeenUtc,
                item.LastSeenUtc,
                todayActiveSeconds = usage.GetValueOrDefault((item.Id, today)),
                hasIcon = withIcon.Contains(item.Id),
                // The panel hides Windows' own applications behind a switch. The classification is
                // made here rather than in the browser because only the server sees the publisher
                // and package family the decision is made from.
                isMicrosoft = ApplicationCatalogPolicy.IsMicrosoftPublished(descriptor),
                item.Rule.ManuallyBlocked,
                dailyLimitSeconds = RuleEvaluator.EffectiveDailyLimitSeconds(item.Rule.DailyLimitSeconds, schedule, bonus, today),
                localDate = DeviceClock.ToLocalDate(today),
                extraTimeUntilLocal = DeviceClock.ExtraTimeUntilLocal(bonus, now, item.Device.TimeZoneId),
                // A schedule is the rule most of these applications actually carry, so the list
                // says whether one is set and whether it is open right now. Without it every row
                // reads "Allowed" while the schedule has the application shut.
                scheduleConfigured = schedule.IsConfigured,
                withinSchedule = RuleEvaluator.IsWithinSchedule(schedule, now, item.Device.TimeZoneId)
            });
        }

        return Ok(result);
    }

    [HttpGet("{deviceApplicationId:guid}")]
    public async Task<IActionResult> Get(Guid deviceApplicationId, CancellationToken cancellationToken)
    {
        var item = await dbContext.DeviceApplications.AsNoTracking()
            .Include(x => x.Device).Include(x => x.Application).Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (item is null) return NotFound();
        if (!ApplicationCatalogPolicy.IsUserManageable(item.ToDescriptor())) return NotFound();
        var usage = await dbContext.DailyApplicationUsages.AsNoTracking()
            .Where(x => x.DeviceApplicationId == item.Id)
            .OrderByDescending(x => x.LocalDate).Take(60)
            .OrderBy(x => x.LocalDate)
            .Select(x => new { date = x.LocalDate, activeSeconds = x.ActiveSeconds })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            id = item.Id,
            device = new { item.Device.Id, item.Device.Name },
            application = new
            {
                item.Application.Id,
                item.Application.IdentityKey,
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(item.ToDescriptor()),
                item.Application.ExecutableName,
                item.Application.ProductName,
                item.Application.OriginalFilename,
                item.Application.Company,
                item.Application.SignaturePublisher,
                item.Application.PackageFamilyName
            },
            item.ExecutablePath,
            item.FileVersion,
            item.Sha256,
            item.FirstSeenUtc,
            item.LastSeenUtc,
            hasIcon = item.IconPng is { Length: > 0 },
            rule = new
            {
                item.Rule.ManuallyBlocked,
                item.Rule.DailyLimitSeconds,
                schedule = RuleSnapshotFactory.DeserializeSchedule(item.Rule.ScheduleJson)
            },
            usage
        });
    }

    [HttpGet("{deviceApplicationId:guid}/icon")]
    public async Task<IActionResult> GetIcon(Guid deviceApplicationId, CancellationToken cancellationToken)
    {
        var icon = await dbContext.DeviceApplications.AsNoTracking()
            .Where(item => item.Id == deviceApplicationId)
            .Select(item => item.IconPng)
            .SingleOrDefaultAsync(cancellationToken);
        if (icon is not { Length: > 0 }) return NotFound();
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(icon, "image/png");
    }

    [HttpPut("{deviceApplicationId:guid}/rules")]
    public async Task<IActionResult> UpdateRule(
        Guid deviceApplicationId,
        UpdateApplicationRuleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Schedule is not null && ScheduleValidator.Validate(request.Schedule) is { } scheduleError)
            return BadRequest(scheduleError);
        var item = await dbContext.DeviceApplications.Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (item is null) return NotFound();
        item.Rule.ManuallyBlocked = request.ManuallyBlocked;
        if (request.Schedule is not null) item.Rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(request.Schedule);
        await MarkApplicationRuleChangedAsync(item, cancellationToken);
        return NoContent();
    }

    /// <summary>Replaces one application's daily limit and nothing else; null removes it.</summary>
    [HttpPut("{deviceApplicationId:guid}/limit")]
    public async Task<IActionResult> UpdateLimit(
        Guid deviceApplicationId,
        UpdateLimitRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DailyLimitSeconds is < 60 or > 86_400) return BadRequest("Daily limit must be 60-86400 seconds.");
        var item = await dbContext.DeviceApplications.Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (item is null) return NotFound();
        item.Rule.DailyLimitSeconds = request.DailyLimitSeconds;
        await MarkApplicationRuleChangedAsync(item, cancellationToken);
        return NoContent();
    }

    /// <summary>Replaces one application's weekly schedule and nothing else.</summary>
    [HttpPut("{deviceApplicationId:guid}/schedule")]
    public async Task<IActionResult> UpdateSchedule(
        Guid deviceApplicationId,
        WeeklySchedule schedule,
        CancellationToken cancellationToken)
    {
        if (ScheduleValidator.Validate(schedule) is { } scheduleError) return BadRequest(scheduleError);
        var item = await dbContext.DeviceApplications.Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (item is null) return NotFound();
        item.Rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(schedule);
        await MarkApplicationRuleChangedAsync(item, cancellationToken);
        return NoContent();
    }

    /// <summary>An application's rule changed, which is a change to its PC's rules.</summary>
    private Task MarkApplicationRuleChangedAsync(DeviceApplication item, CancellationToken cancellationToken)
    {
        item.Rule.UpdatedAtUtc = timeProvider.GetUtcNow();
        return ruleChanges.PublishAsync(item.DeviceId, cancellationToken);
    }
}
