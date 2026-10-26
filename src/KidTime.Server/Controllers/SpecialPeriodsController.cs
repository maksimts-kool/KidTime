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

    /// <summary>A period's contents, as the add and edit popups send them.</summary>
    public sealed record UpdatePeriodRequest(
        string Name,
        DateOnly StartDate,
        DateOnly EndDate,
        WeeklySchedule Schedule,
        int? DailyLimitSeconds);

    [HttpPost]
    public async Task<IActionResult> Create(CreatePeriodRequest request, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
        if (device is null) return NotFound();
        if (request.DeviceApplicationId is { } applicationId
            && !await dbContext.DeviceApplications.AnyAsync(
                item => item.Id == applicationId && item.DeviceId == request.DeviceId, cancellationToken))
            return NotFound();

        var period = new SpecialPeriod
        {
            DeviceId = request.DeviceId,
            DeviceApplicationId = request.DeviceApplicationId,
            Name = string.Empty,
            CreatedAtUtc = timeProvider.GetUtcNow()
        };
        var contents = new UpdatePeriodRequest(
            request.Name, request.StartDate, request.EndDate, request.Schedule, request.DailyLimitSeconds);
        if (await ApplyAsync(period, contents, device.TimeZoneId, cancellationToken) is { } error) return BadRequest(error);
        dbContext.SpecialPeriods.Add(period);
        await ruleChanges.PublishAsync(request.DeviceId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Replaces a period's name, dates, schedule and limit. A period already in force can be
    /// changed too - a holiday extended by a day, or its evening moved - and lands like any rule.
    /// </summary>
    [HttpPut("{periodId:guid}")]
    public async Task<IActionResult> Update(Guid periodId, UpdatePeriodRequest request, CancellationToken cancellationToken)
    {
        var period = await dbContext.SpecialPeriods.Include(x => x.Device)
            .SingleOrDefaultAsync(x => x.Id == periodId, cancellationToken);
        if (period is null) return NotFound();
        if (await ApplyAsync(period, request, period.Device.TimeZoneId, cancellationToken) is { } error) return BadRequest(error);
        await ruleChanges.PublishAsync(period.DeviceId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Validates <paramref name="request"/> against the PC's today and the other periods for the
    /// same scope, and writes it onto <paramref name="period"/> when it passes.
    /// </summary>
    private async Task<string?> ApplyAsync(
        SpecialPeriod period,
        UpdatePeriodRequest request,
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        var today = RuleEvaluator.GetLocalDate(timeProvider.GetUtcNow(), timeZoneId);
        if (request.EndDate < today) return "The period has already ended.";

        var candidate = new ScheduleOverride
        {
            Name = request.Name?.Trim() ?? string.Empty,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Days = request.Schedule.Days,
            DailyLimitSeconds = request.DailyLimitSeconds
        };
        if (candidate.Name.Length > 100) return "Keep the name under 100 characters.";
        var others = await dbContext.SpecialPeriods.AsNoTracking()
            .Where(other => other.Id != period.Id
                            && other.DeviceId == period.DeviceId
                            && other.DeviceApplicationId == period.DeviceApplicationId
                            && other.EndDate >= today)
            .ToListAsync(cancellationToken);
        if (ScheduleValidator.ValidateOverride(candidate, others.Select(RuleSnapshotFactory.ToOverride)) is { } error)
            return error;

        period.Name = candidate.Name;
        period.StartDate = candidate.StartDate;
        period.EndDate = candidate.EndDate;
        period.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(new WeeklySchedule { Days = candidate.Days });
        period.DailyLimitSeconds = candidate.DailyLimitSeconds;
        return null;
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
