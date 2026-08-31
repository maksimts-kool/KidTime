using KidTime.Domain.Applications;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using KidTime.Server.Hubs;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/applications")]
public sealed class ApplicationsController(
    KidTimeDbContext dbContext,
    IHubContext<DeviceHub> hubContext,
    TimeProvider timeProvider) : ControllerBase
{
    public sealed record UpdateApplicationRuleRequest(
        bool ManuallyBlocked,
        int? DailyLimitSeconds,
        WeeklySchedule Schedule);

    [HttpGet]
    public async Task<IActionResult> List(Guid? deviceId, string? search, CancellationToken cancellationToken)
    {
        var query = dbContext.DeviceApplications.AsNoTracking()
            .Include(x => x.Device)
            .Include(x => x.Application)
            .Include(x => x.Rule)
            .AsQueryable();
        if (deviceId is not null) query = query.Where(x => x.DeviceId == deviceId);
        var applications = (await query.ToListAsync(cancellationToken))
            .Where(item => ApplicationCatalogPolicy.IsUserManageable(ToDescriptor(item)))
            .Where(item => string.IsNullOrWhiteSpace(search)
                           || ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item))
                               .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                           || item.Application.ExecutableName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => new
            {
                item.DeviceId,
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)).ToUpperInvariant()
            })
            .Select(group => group.OrderByDescending(item => item.LastSeenUtc).First())
            .OrderBy(item => ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)))
            .ToList();
        var result = new List<object>(applications.Count);
        var now = timeProvider.GetUtcNow();
        foreach (var item in applications)
        {
            var today = RuleEvaluator.GetLocalDate(now, item.Device.TimeZoneId);
            var schedule = RuleSnapshotFactory.DeserializeSchedule(item.Rule.ScheduleJson);
            var seconds = await dbContext.DailyApplicationUsages.AsNoTracking()
                .Where(x => x.DeviceApplicationId == item.Id && x.LocalDate == today)
                .Select(x => (int?)x.ActiveSeconds).SingleOrDefaultAsync(cancellationToken) ?? 0;
            result.Add(new
            {
                id = item.Id,
                deviceId = item.DeviceId,
                deviceName = item.Device.Name,
                identityKey = item.Application.IdentityKey,
                displayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)),
                executableName = item.Application.ExecutableName,
                item.ExecutablePath,
                item.FileVersion,
                publisher = item.Application.SignaturePublisher ?? item.Application.Company,
                item.FirstSeenUtc,
                item.LastSeenUtc,
                todayActiveSeconds = seconds,
                hasIcon = item.IconPng is { Length: > 0 },
                // The panel hides Windows' own applications behind a switch. The classification is
                // made here rather than in the browser because only the server sees the publisher
                // and package family the decision is made from.
                isMicrosoft = ApplicationCatalogPolicy.IsMicrosoftPublished(ToDescriptor(item)),
                item.Rule.ManuallyBlocked,
                item.Rule.DailyLimitSeconds,
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
        if (!ApplicationCatalogPolicy.IsUserManageable(ToDescriptor(item))) return NotFound();
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
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(ToDescriptor(item)),
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
        if (request.DailyLimitSeconds is < 60 or > 86_400) return BadRequest("Daily limit must be 60-86400 seconds.");
        if (ScheduleValidator.Validate(request.Schedule) is { } scheduleError) return BadRequest(scheduleError);
        var item = await dbContext.DeviceApplications.Include(x => x.Rule)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (item is null) return NotFound();
        item.Rule.ManuallyBlocked = request.ManuallyBlocked;
        item.Rule.DailyLimitSeconds = request.DailyLimitSeconds;
        item.Rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(request.Schedule);
        item.Rule.UpdatedAtUtc = timeProvider.GetUtcNow();
        var deviceRule = await dbContext.DeviceRules.SingleAsync(x => x.DeviceId == item.DeviceId, cancellationToken);
        deviceRule.Revision++;
        deviceRule.UpdatedAtUtc = timeProvider.GetUtcNow();
        var command = new DeviceCommand { DeviceId = item.DeviceId, Type = "RulesChanged" };
        dbContext.DeviceCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.Group(DeviceHub.GroupName(item.DeviceId))
            .SendAsync("Command", new { command.Id, command.Type }, cancellationToken);
        return NoContent();
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
}
