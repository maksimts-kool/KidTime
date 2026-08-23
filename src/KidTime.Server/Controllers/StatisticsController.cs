using KidTime.Server.Data;
using KidTime.Domain.Applications;
using KidTime.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/statistics")]
public sealed class StatisticsController(KidTimeDbContext dbContext, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("devices/{deviceId:guid}")]
    public async Task<IActionResult> Device(
        Guid deviceId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return NotFound();
        var end = to ?? RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), device.TimeZoneId);
        var start = from ?? end.AddDays(-6);
        if (end < start || end.DayNumber - start.DayNumber > 370) return BadRequest("Invalid date range.");

        var daily = await dbContext.DailyDeviceUsages.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.LocalDate >= start && x.LocalDate <= end)
            .OrderBy(x => x.LocalDate)
            .Select(x => new { date = x.LocalDate, activeSeconds = x.ActiveSeconds })
            .ToListAsync(cancellationToken);
        var applicationUsage = await dbContext.DailyApplicationUsages.AsNoTracking()
            .Include(x => x.DeviceApplication)
            .ThenInclude(x => x.Application)
            .Where(x => x.DeviceApplication.DeviceId == deviceId && x.LocalDate >= start && x.LocalDate <= end)
            .ToListAsync(cancellationToken);
        var applications = applicationUsage
            .Select(item => new { Usage = item, Descriptor = ToDescriptor(item.DeviceApplication) })
            .Where(item => ApplicationCatalogPolicy.IsUserManageable(item.Descriptor))
            .GroupBy(item => new
            {
                IdentityKey = ApplicationIdentity.CreateKey(ApplicationCatalogPolicy.NormalizeForCatalog(item.Descriptor)),
                DisplayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(item.Descriptor)
            })
            .Select(group => new
            {
                displayName = group.Key.DisplayName,
                identityKey = group.Key.IdentityKey,
                activeSeconds = group.Sum(x => x.Usage.ActiveSeconds)
            })
            .OrderByDescending(x => x.activeSeconds).Take(20).ToList();
        return Ok(new { from = start, to = end, totalActiveSeconds = daily.Sum(x => x.activeSeconds), daily, applications });
    }

    [HttpGet("applications/{deviceApplicationId:guid}")]
    public async Task<IActionResult> Application(
        Guid deviceApplicationId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        var app = await dbContext.DeviceApplications.AsNoTracking()
            .Include(x => x.Application)
            .Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (app is null) return NotFound();
        var end = to ?? RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), app.Device.TimeZoneId);
        var start = from ?? end.AddDays(-27);
        if (end < start || end.DayNumber - start.DayNumber > 370) return BadRequest("Invalid date range.");
        var daily = await dbContext.DailyApplicationUsages.AsNoTracking()
            .Where(x => x.DeviceApplicationId == deviceApplicationId && x.LocalDate >= start && x.LocalDate <= end)
            .OrderBy(x => x.LocalDate)
            .Select(x => new { date = x.LocalDate, activeSeconds = x.ActiveSeconds })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            app.Application.DisplayName,
            from = start,
            to = end,
            totalActiveSeconds = daily.Sum(x => x.activeSeconds),
            daily
        });
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
