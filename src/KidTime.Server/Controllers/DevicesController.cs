using KidTime.Domain.Rules;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;
using KidTime.Server.Data;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController(
    KidTimeDbContext dbContext,
    RuleSnapshotFactory snapshots,
    AgentUpdateCatalog updates,
    RuleChangePublisher ruleChanges,
    TimeProvider timeProvider,
    ILogger<DevicesController> logger) : ControllerBase
{
    /// <summary>
    /// The PC's rule form. The schedule and the daily limit are not on it: the panel edits both in
    /// the schedule card, through PUT schedule and PUT limit, and saves each on its own.
    /// </summary>
    public sealed record UpdateDeviceRuleRequest(
        int IdleThresholdSeconds,
        // Null keeps the stored schedule: the panel edits it on its own, in a popup that saves
        // through PUT schedule, so the rest of the rule form no longer carries it.
        WeeklySchedule? Schedule,
        string? ControlledUserSid,
        AgentLanguage? Language = null,
        bool? OpenWindowAtSignIn = null);

    public sealed record BlockDeviceRequest(int? Minutes, DateTimeOffset? UntilUtc);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var latestAgentVersion = updates.GetLatest()?.Manifest.Version;
        var devices = await dbContext.Devices.AsNoTracking().Include(x => x.Rule).ToListAsync(cancellationToken);
        var periods = (await RuleSnapshotFactory.ActivePeriods(dbContext.SpecialPeriods.AsNoTracking(), now)
                .Where(period => period.DeviceApplicationId == null)
                .ToListAsync(cancellationToken))
            .ToLookup(period => period.DeviceId);
        var faultCounts = await dbContext.DeviceDiagnosticEvents.AsNoTracking()
            .Where(item => item.ResolvedAtUtc == null)
            .GroupBy(item => item.DeviceId)
            .Select(group => new { DeviceId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.DeviceId, entry => entry.Count, cancellationToken);
        var grants = (await RuleSnapshotFactory.RecentGrants(dbContext.TimeExtensions.AsNoTracking(), now)
                .Where(grant => grant.ApplicationIdentityKey == null)
                .ToListAsync(cancellationToken))
            .ToLookup(grant => grant.DeviceId);
        var localDates = devices.ToDictionary(device => device.Id, device => RuleEvaluator.GetLocalDate(now, device.TimeZoneId));
        var earliestDate = localDates.Values.DefaultIfEmpty().Min();
        var usage = (await dbContext.DailyDeviceUsages.AsNoTracking()
                .Where(x => x.LocalDate >= earliestDate)
                .Select(x => new { x.DeviceId, x.LocalDate, x.ActiveSeconds })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => (x.DeviceId, x.LocalDate), x => x.ActiveSeconds);
        var result = new List<object>(devices.Count);
        foreach (var device in devices)
        {
            var localDate = localDates[device.Id];
            var today = usage.GetValueOrDefault((device.Id, localDate));
            var schedule = RuleSnapshotFactory.Compose(device.Rule.ScheduleJson, periods[device.Id]);
            // The limit in force today: a holiday can carry one of its own, and extra time the
            // parent gave raises it - the same figure the PC enforces, so a card never says
            // "limit reached" about a PC the child has just been given more time on.
            var bonus = RuleSnapshotFactory.BonusFor(grants[device.Id], localDate, now);
            var dailyLimit = RuleEvaluator.EffectiveDailyLimitSeconds(device.Rule.DailyLimitSeconds, schedule, bonus, localDate);
            result.Add(new
            {
                device.Id,
                device.Name,
                device.WindowsVersion,
                device.TimeZoneId,
                isOnline = device.LastSeenUtc >= now.AddMinutes(-2),
                device.LastSeenUtc,
                device.LoggedInUser,
                device.ForegroundApplication,
                controlledUserName = device.Rule.ControlledUserName,
                language = device.Rule.Language,
                openWindowAtSignIn = device.Rule.OpenWindowAtSignIn,
                device.AgentVersion,
                latestAgentVersion,
                device.AgentUpdateStatus,
                device.AgentUpdateError,
                device.AgentUpdateCheckedAtUtc,
                isAgentUpToDate = updates.IsCurrent(device.AgentVersion),
                todayActiveSeconds = today,
                dailyLimitSeconds = dailyLimit,
                remainingSeconds = dailyLimit is int limit ? Math.Max(0, limit - today) : (int?)null,
                schedule = DescribeSchedule(schedule, device.TimeZoneId, localDate, now),
                manuallyBlocked = device.Rule.ManuallyBlocked &&
                    (device.Rule.ManualBlockUntilUtc is null || device.Rule.ManualBlockUntilUtc > now),
                device.Rule.ManualBlockUntilUtc,
                // A grant given while the PC was blocked or outside its schedule opens it until
                // then, so the card says so rather than "Blocked by you".
                extraTimeUntilLocal = DeviceClock.ExtraTimeUntilLocal(bonus, now, device.TimeZoneId),
                ruleRevision = device.Rule.Revision,
                appliedRuleRevision = device.AppliedRuleRevision,
                unresolvedFaults = faultCounts.GetValueOrDefault(device.Id)
            });
        }

        return Ok(result);
    }

    [HttpGet("{deviceId:guid}")]
    public async Task<IActionResult> Get(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await dbContext.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return NotFound();
        var now = timeProvider.GetUtcNow();
        var latestAgentVersion = updates.GetLatest()?.Manifest.Version;
        return Ok(new
        {
            device = new
            {
                device.Id,
                device.Name,
                device.WindowsVersion,
                device.TimeZoneId,
                device.EnrolledAtUtc,
                device.LastSeenUtc,
                device.LoggedInUser,
                device.ForegroundApplication,
                device.AgentVersion,
                latestAgentVersion,
                device.AgentUpdateStatus,
                device.AgentUpdateError,
                device.AgentUpdateCheckedAtUtc,
                isAgentUpToDate = updates.IsCurrent(device.AgentVersion),
                device.AppliedRuleRevision,
                isOnline = device.LastSeenUtc >= now.AddMinutes(-2)
            },
            rules = await snapshots.CreateAsync(deviceId, cancellationToken),
            availableWindowsUsers = DeserializeWindowsUsers(device.WindowsUsersJson)
        });
    }

    [HttpDelete("{deviceId:guid}")]
    public async Task<IActionResult> Delete(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var device = await dbContext.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return NotFound();

        var applicationIds = await dbContext.DeviceApplications.AsNoTracking()
            .Where(x => x.DeviceId == deviceId)
            .Select(x => x.ApplicationId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var usageBatches = await dbContext.ProcessedUsageBatches
            .Where(x => x.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        var enrollmentTokens = await dbContext.EnrollmentTokens
            .Where(x => x.EnrolledDeviceId == deviceId)
            .ToListAsync(cancellationToken);

        dbContext.ProcessedUsageBatches.RemoveRange(usageBatches);
        dbContext.EnrollmentTokens.RemoveRange(enrollmentTokens);
        dbContext.Devices.Remove(device);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (applicationIds.Count > 0)
        {
            var orphanedApplications = await dbContext.Applications
                .Where(x => applicationIds.Contains(x.Id) && !x.Devices.Any())
                .ToListAsync(cancellationToken);
            dbContext.Applications.RemoveRange(orphanedApplications);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Removed device {DeviceName} ({DeviceId}) and revoked its credentials.", device.Name, device.Id);
        return NoContent();
    }

    [HttpPut("{deviceId:guid}/rules")]
    public async Task<IActionResult> UpdateRules(
        Guid deviceId,
        UpdateDeviceRuleRequest request,
        CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        if (request.Schedule is not null && ScheduleValidator.Validate(request.Schedule) is { } scheduleError)
            return BadRequest(scheduleError);

        var device = await dbContext.Devices.AsNoTracking().SingleAsync(x => x.Id == deviceId, cancellationToken);
        var users = DeserializeWindowsUsers(device.WindowsUsersJson);
        var selectedUser = string.IsNullOrWhiteSpace(request.ControlledUserSid)
            ? null
            : users.FirstOrDefault(user => string.Equals(user.Sid, request.ControlledUserSid.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(request.ControlledUserSid) && selectedUser is null)
            return BadRequest("Choose a Windows account reported by this device.");
        if (selectedUser is { IsEnabled: false })
            return BadRequest("The selected Windows account is disabled.");
        if (selectedUser is { IsAdministrator: true })
            return BadRequest("Choose a Standard User account, not an administrator.");

        if (request.Language is { } language && !Enum.IsDefined(language))
            return BadRequest("Choose a supported interface language.");

        rule.IdleThresholdSeconds = Math.Clamp(request.IdleThresholdSeconds, 30, 3_600);
        rule.Language = request.Language ?? rule.Language;
        rule.OpenWindowAtSignIn = request.OpenWindowAtSignIn ?? rule.OpenWindowAtSignIn;
        if (request.Schedule is not null) rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(request.Schedule);
        rule.ControlledUserSid = selectedUser?.Sid;
        rule.ControlledUserName = selectedUser?.AccountName;
        await ruleChanges.PublishAsync(rule, cancellationToken);
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }

    /// <summary>
    /// Replaces the PC's weekly schedule and nothing else. The panel adds and edits ranges in
    /// popups that save at once, so each one is a whole-week write of the week as the popup left it.
    /// </summary>
    [HttpPut("{deviceId:guid}/schedule")]
    public async Task<IActionResult> UpdateSchedule(
        Guid deviceId,
        WeeklySchedule schedule,
        CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        if (ScheduleValidator.Validate(schedule) is { } scheduleError) return BadRequest(scheduleError);
        rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(schedule);
        await ruleChanges.PublishAsync(rule, cancellationToken);
        return NoContent();
    }

    public sealed record UpdateLimitRequest(int? DailyLimitSeconds);

    /// <summary>Replaces the PC's daily limit and nothing else; null removes it.</summary>
    [HttpPut("{deviceId:guid}/limit")]
    public async Task<IActionResult> UpdateLimit(
        Guid deviceId,
        UpdateLimitRequest request,
        CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        if (request.DailyLimitSeconds is < 60 or > 86_400) return BadRequest("Daily limit must be 60-86400 seconds.");
        rule.DailyLimitSeconds = request.DailyLimitSeconds;
        await ruleChanges.PublishAsync(rule, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// What the weekly schedule means right now, in the shape the panel reads it: whether a window
    /// is open, when the open one closes, when the next one opens, and the windows standing for
    /// today. A household that governs the PC by schedule rather than by a daily total learns
    /// nothing from "No limit", so the summary carries the schedule instead of leaving the panel
    /// to walk the raw week itself.
    ///
    /// Times cross as the controlled PC's own wall clock, not as instants. The device reports a
    /// Windows timezone id ("Russian Standard Time"), which .NET resolves and JavaScript's Intl
    /// does not - handed a UTC instant and that id, the panel silently rendered UTC and told the
    /// parent a window closed three hours before it does.
    /// </summary>
    private static object DescribeSchedule(
        WeeklySchedule schedule,
        string timeZoneId,
        DateOnly localDate,
        DateTimeOffset now)
    {
        var withinWindow = RuleEvaluator.IsWithinSchedule(schedule, now, timeZoneId);
        var localNow = RuleEvaluator.ToLocalTime(now, timeZoneId);
        var period = schedule.OverrideOn(localDate);
        return new
        {
            configured = schedule.IsConfigured,
            // The holiday or trip whose rules are in force today, so the card can say why today
            // does not look like the weekly schedule.
            specialPeriod = period is null
                ? null
                : new { period.Name, endDate = period.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            withinWindow,
            localDate = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            nowMinuteOfDay = localNow.Hour * 60 + localNow.Minute,
            closesAtLocal = withinWindow
                ? DeviceClock.ToLocalStamp(RuleEvaluator.FindCurrentAllowanceEndUtc(schedule, now, timeZoneId), timeZoneId)
                : null,
            opensAtLocal = withinWindow
                ? null
                : DeviceClock.ToLocalStamp(RuleEvaluator.FindNextAllowanceStartUtc(schedule, now, timeZoneId), timeZoneId),
            todayWindows = (period?.Days ?? schedule.Days)
                .Where(day => day.Day == localDate.DayOfWeek)
                .SelectMany(day => day.Windows)
                .OrderBy(window => window.Start)
                .Select(window => $"{ToClock(window.Start)}–{ToClock(window.End)}")
                .ToList()
        };
    }

    /// <summary>An instant as an unzoned device-local stamp, "2026-08-31T21:00".</summary>

    private static string ToClock(TimeOnly time) =>
        time.ToString("HH':'mm", CultureInfo.InvariantCulture);

    private static IReadOnlyList<WindowsUserAccount> DeserializeWindowsUsers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<WindowsUserAccount>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException) { return []; }
    }

    [HttpPost("{deviceId:guid}/block")]
    public async Task<IActionResult> Block(Guid deviceId, BlockDeviceRequest request, CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        var now = timeProvider.GetUtcNow();
        rule.ManuallyBlocked = true;
        rule.ManualBlockUntilUtc = request.UntilUtc ?? (request.Minutes is int minutes
            ? now.AddMinutes(Math.Clamp(minutes, 1, 10_080))
            : null);
        await ruleChanges.PublishAsync(rule, cancellationToken, "BlockPc");
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }

    [HttpPost("{deviceId:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid deviceId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        rule.ManuallyBlocked = false;
        rule.ManualBlockUntilUtc = null;
        await ruleChanges.PublishAsync(rule, cancellationToken, "UnblockPc");
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }
}
