using System.Globalization;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

/// <summary>
/// Holidays, trips and other dates that run on rules of their own. A period is a rule like any
/// other - adding or removing one bumps the device's rule revision, queues a command and is pushed
/// over the hub - and it reaches the PC inside the snapshot ahead of time, so the switch at
/// midnight on its first and last day happens on the PC's own clock.
/// </summary>
[ApiController]
[Authorize]
[Route("api/special-periods")]
public sealed class SpecialPeriodsController(
    KidTimeDbContext dbContext,
    RuleChangePublisher ruleChanges,
    TimeProvider timeProvider) : ControllerBase
{
    public sealed record CreatePeriodRequest(
        Guid DeviceId,
        Guid? DeviceApplicationId,
        string Name,
        DateOnly StartDate,
        DateOnly EndDate,
        WeeklySchedule Schedule,
        int? DailyLimitSeconds);

    /// <summary>
    /// The periods for the PC's own screen time, or for one application when
    /// <paramref name="deviceApplicationId"/> is given - current and upcoming, soonest first.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid deviceId, Guid? deviceApplicationId, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return NotFound();
        var now = timeProvider.GetUtcNow();
        var today = RuleEvaluator.GetLocalDate(now, device.TimeZoneId);
        var periods = await dbContext.SpecialPeriods.AsNoTracking()
            .Where(period => period.DeviceId == deviceId
                             && period.DeviceApplicationId == deviceApplicationId
                             && period.EndDate >= today)
            .OrderBy(period => period.StartDate)
            .ToListAsync(cancellationToken);
        return Ok(periods.Select(period => new
        {
            period.Id,
            period.Name,
            startDate = period.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = period.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            schedule = new WeeklySchedule { Days = RuleSnapshotFactory.DeserializeSchedule(period.ScheduleJson).Days },
            period.DailyLimitSeconds,
            isActive = period.StartDate <= today
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreatePeriodRequest request, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
        if (device is null) return NotFound();
        if (request.DeviceApplicationId is { } applicationId
            && !await dbContext.DeviceApplications.AnyAsync(
                item => item.Id == applicationId && item.DeviceId == request.DeviceId, cancellationToken))
            return NotFound();

        var today = RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), device.TimeZoneId);
        if (request.EndDate < today) return BadRequest("The period has already ended.");

        var candidate = new ScheduleOverride
        {
            Name = request.Name?.Trim() ?? string.Empty,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Days = request.Schedule.Days,
            DailyLimitSeconds = request.DailyLimitSeconds
        };
        if (candidate.Name.Length > 100) return BadRequest("Keep the name under 100 characters.");
        var existing = await dbContext.SpecialPeriods.AsNoTracking()
            .Where(period => period.DeviceId == request.DeviceId
                             && period.DeviceApplicationId == request.DeviceApplicationId
                             && period.EndDate >= today)
            .ToListAsync(cancellationToken);
        if (ScheduleValidator.ValidateOverride(candidate, existing.Select(RuleSnapshotFactory.ToOverride)) is { } error)
            return BadRequest(error);

        dbContext.SpecialPeriods.Add(new SpecialPeriod
        {
            DeviceId = request.DeviceId,
            DeviceApplicationId = request.DeviceApplicationId,
            Name = candidate.Name,
            StartDate = candidate.StartDate,
            EndDate = candidate.EndDate,
            ScheduleJson = RuleSnapshotFactory.SerializeSchedule(new WeeklySchedule { Days = candidate.Days }),
            DailyLimitSeconds = candidate.DailyLimitSeconds,
            CreatedAtUtc = timeProvider.GetUtcNow()
        });
        await ruleChanges.PublishAsync(request.DeviceId, cancellationToken);
        return NoContent();
    }

    [HttpDelete("{periodId:guid}")]
    public async Task<IActionResult> Delete(Guid periodId, CancellationToken cancellationToken)
    {
        var period = await dbContext.SpecialPeriods.SingleOrDefaultAsync(x => x.Id == periodId, cancellationToken);
        if (period is null) return NotFound();
        dbContext.SpecialPeriods.Remove(period);
        await ruleChanges.PublishAsync(period.DeviceId, cancellationToken);
        return NoContent();
    }
}
