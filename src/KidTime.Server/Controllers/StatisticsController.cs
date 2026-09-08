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
    /// <summary>
    /// How many applications carry a day-by-day series of their own. The panel stacks them into
    /// one chart and folds the rest into a single "other" band, so this bounds the payload without
    /// losing any time from the totals.
    /// </summary>
    private const int ChartedApplications = 6;

    private const int ListedApplications = 25;

    [HttpGet("devices/{deviceId:guid}")]
    public async Task<IActionResult> Device(
        Guid deviceId,
        DateOnly? from,
        DateOnly? to,
        int? days,
        CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return NotFound();
        if (days is < 1 or > 370) return BadRequest("Invalid day count.");
        var end = to ?? RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), device.TimeZoneId);
        // A length rather than a start date, because only the server knows which day the PC is
        // standing in - the panel deliberately does not resolve the device timezone itself.
        var start = from ?? end.AddDays(-((days ?? 7) - 1));
        if (end < start || end.DayNumber - start.DayNumber > 370) return BadRequest("Invalid date range.");

        // The same stretch of days immediately before the one asked for. Everything on this page
        // is a number a parent has to judge, and "6 h" means one thing after a quiet week and
        // another after a loud one, so each total is reported against its own previous period.
        var length = end.DayNumber - start.DayNumber + 1;
        var previousStart = start.AddDays(-length);

        var deviceUsage = await dbContext.DailyDeviceUsages.AsNoTracking()
            .Where(x => x.DeviceId == deviceId && x.LocalDate >= previousStart && x.LocalDate <= end)
            .Select(x => new { x.LocalDate, x.ActiveSeconds })
            .ToListAsync(cancellationToken);
        var byDate = deviceUsage
            .Where(x => x.LocalDate >= start)
            .ToDictionary(x => x.LocalDate, x => x.ActiveSeconds);
        // Every day in the range appears, including the ones with nothing on them: a chart that
        // silently drops a quiet Tuesday reads as a week that was busier than it was.
        var daily = Enumerable.Range(0, length)
            .Select(offset => start.AddDays(offset))
            .Select(date => new { date, activeSeconds = byDate.GetValueOrDefault(date) })
            .ToList();
        var total = daily.Sum(x => x.activeSeconds);
        var previousTotal = deviceUsage.Where(x => x.LocalDate < start).Sum(x => x.ActiveSeconds);

        // Icons are excluded from the projection deliberately - the page needs to know only that
        // one exists, and the bytes are fetched per application by the browser.
        var catalog = (await dbContext.DeviceApplications.AsNoTracking()
                .Where(x => x.DeviceId == deviceId)
                .Select(x => new
                {
                    x.Id,
                    x.ExecutablePath,
                    x.FileVersion,
                    x.Sha256,
                    x.LastSeenUtc,
                    HasIcon = x.IconPng != null && x.IconPng.Length > 0,
                    Descriptor = new ApplicationDescriptor
                    {
                        DisplayName = x.Application.DisplayName,
                        ExecutableName = x.Application.ExecutableName,
                        ExecutablePath = x.ExecutablePath,
                        ProductName = x.Application.ProductName,
                        OriginalFilename = x.Application.OriginalFilename,
                        Company = x.Application.Company,
                        SignaturePublisher = x.Application.SignaturePublisher,
                        FileVersion = x.FileVersion,
                        PackageFamilyName = x.Application.PackageFamilyName,
                        Sha256 = x.Sha256
                    }
                })
                .ToListAsync(cancellationToken))
            .Where(item => ApplicationCatalogPolicy.IsUserManageable(item.Descriptor))
            .ToDictionary(item => item.Id);

        var applicationUsage = await dbContext.DailyApplicationUsages.AsNoTracking()
            .Where(x => x.DeviceApplication.DeviceId == deviceId
                        && x.LocalDate >= previousStart && x.LocalDate <= end)
            .Select(x => new { x.DeviceApplicationId, x.LocalDate, x.ActiveSeconds })
            .ToListAsync(cancellationToken);

        var applications = applicationUsage
            .Where(usage => catalog.ContainsKey(usage.DeviceApplicationId))
            .Select(usage => new { Usage = usage, Item = catalog[usage.DeviceApplicationId] })
            // A satellite and its principal are one application here for the same reason they are
            // one card on the applications page: the identity key is what a rule is written on.
            .GroupBy(row => ApplicationIdentity.CreateKey(
                ApplicationCatalogPolicy.NormalizeForCatalog(row.Item.Descriptor)))
            .Select(group =>
            {
                var representative = group
                    .Select(row => row.Item)
                    .OrderByDescending(item => item.HasIcon)
                    .ThenByDescending(item => item.LastSeenUtc)
                    .First();
                var current = group.Where(row => row.Usage.LocalDate >= start).ToList();
                return new
                {
                    identityKey = group.Key,
                    deviceApplicationId = representative.Id,
                    displayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(representative.Descriptor),
                    publisher = representative.Descriptor.SignaturePublisher ?? representative.Descriptor.Company,
                    hasIcon = representative.HasIcon,
                    activeSeconds = current.Sum(row => row.Usage.ActiveSeconds),
                    previousActiveSeconds = group.Where(row => row.Usage.LocalDate < start)
                        .Sum(row => row.Usage.ActiveSeconds),
                    daily = current
                        .GroupBy(row => row.Usage.LocalDate)
                        .Select(day => new { date = day.Key, activeSeconds = day.Sum(row => row.Usage.ActiveSeconds) })
                        .OrderBy(day => day.date)
                        .ToList()
                };
            })
            .Where(item => item.activeSeconds > 0)
            .OrderByDescending(item => item.activeSeconds)
            .Take(ListedApplications)
            .ToList();

        return Ok(new
        {
            from = start,
            to = end,
            days = length,
            totalActiveSeconds = total,
            previousTotalActiveSeconds = previousTotal,
            dailyAverageSeconds = total / length,
            chartedApplications = ChartedApplications,
            daily,
            applications
        });
    }

    [HttpGet("applications/{deviceApplicationId:guid}")]
    public async Task<IActionResult> Application(
        Guid deviceApplicationId,
        DateOnly? from,
        DateOnly? to,
        int? days,
        CancellationToken cancellationToken)
    {
        var app = await dbContext.DeviceApplications.AsNoTracking()
            .Include(x => x.Application)
            .Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.Id == deviceApplicationId, cancellationToken);
        if (app is null) return NotFound();
        if (days is < 1 or > 370) return BadRequest("Invalid day count.");
        var end = to ?? RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), app.Device.TimeZoneId);
        var start = from ?? end.AddDays(-((days ?? 14) - 1));
        if (end < start || end.DayNumber - start.DayNumber > 370) return BadRequest("Invalid date range.");

        var length = end.DayNumber - start.DayNumber + 1;
        var previousStart = start.AddDays(-length);
        var usage = await dbContext.DailyApplicationUsages.AsNoTracking()
            .Where(x => x.DeviceApplicationId == deviceApplicationId
                        && x.LocalDate >= previousStart && x.LocalDate <= end)
            .Select(x => new { x.LocalDate, x.ActiveSeconds })
            .ToListAsync(cancellationToken);
        var byDate = usage.Where(x => x.LocalDate >= start).ToDictionary(x => x.LocalDate, x => x.ActiveSeconds);
        // Days with nothing on them are part of the answer: a chart drawn only from the days an
        // application ran makes an occasional application look like a daily one.
        var daily = Enumerable.Range(0, length)
            .Select(offset => start.AddDays(offset))
            .Select(date => new { date, activeSeconds = byDate.GetValueOrDefault(date) })
            .ToList();
        var total = daily.Sum(x => x.activeSeconds);
        return Ok(new
        {
            app.Application.DisplayName,
            from = start,
            to = end,
            days = length,
            totalActiveSeconds = total,
            previousTotalActiveSeconds = usage.Where(x => x.LocalDate < start).Sum(x => x.ActiveSeconds),
            dailyAverageSeconds = total / length,
            daily
        });
    }
}
