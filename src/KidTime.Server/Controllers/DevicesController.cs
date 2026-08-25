using KidTime.Domain.Rules;
using KidTime.Domain.Contracts;
using KidTime.Server.Data;
using KidTime.Server.Hubs;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace KidTime.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/devices")]
public sealed class DevicesController(
    KidTimeDbContext dbContext,
    RuleSnapshotFactory snapshots,
    AgentUpdateCatalog updates,
    IHubContext<DeviceHub> hubContext,
    TimeProvider timeProvider,
    ILogger<DevicesController> logger) : ControllerBase
{
    public sealed record UpdateDeviceRuleRequest(
        int? DailyLimitSeconds,
        int IdleThresholdSeconds,
        WeeklySchedule Schedule,
        string? ControlledUserSid);

    public sealed record BlockDeviceRequest(int? Minutes, DateTimeOffset? UntilUtc);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var latestAgentVersion = updates.GetLatest()?.Manifest.Version;
        var devices = await dbContext.Devices.AsNoTracking().Include(x => x.Rule).ToListAsync(cancellationToken);
        var faultCounts = await dbContext.DeviceDiagnosticEvents.AsNoTracking()
            .Where(item => item.ResolvedAtUtc == null)
            .GroupBy(item => item.DeviceId)
            .Select(group => new { DeviceId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.DeviceId, entry => entry.Count, cancellationToken);
        var result = new List<object>(devices.Count);
        foreach (var device in devices)
        {
            var localDate = RuleEvaluator.GetLocalDate(now, device.TimeZoneId);
            var today = await dbContext.DailyDeviceUsages.AsNoTracking()
                .Where(x => x.DeviceId == device.Id && x.LocalDate == localDate)
                .Select(x => (int?)x.ActiveSeconds)
                .SingleOrDefaultAsync(cancellationToken) ?? 0;
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
                device.AgentVersion,
                latestAgentVersion,
                device.AgentUpdateStatus,
                device.AgentUpdateError,
                device.AgentUpdateCheckedAtUtc,
                isAgentUpToDate = updates.IsCurrent(device.AgentVersion),
                todayActiveSeconds = today,
                dailyLimitSeconds = device.Rule.DailyLimitSeconds,
                remainingSeconds = device.Rule.DailyLimitSeconds is int limit ? Math.Max(0, limit - today) : (int?)null,
                manuallyBlocked = device.Rule.ManuallyBlocked &&
                    (device.Rule.ManualBlockUntilUtc is null || device.Rule.ManualBlockUntilUtc > now),
                device.Rule.ManualBlockUntilUtc,
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
        if (request.DailyLimitSeconds is < 60 or > 86_400) return BadRequest("Daily limit must be 60-86400 seconds.");
        if (ScheduleValidator.Validate(request.Schedule) is { } scheduleError) return BadRequest(scheduleError);

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

        rule.DailyLimitSeconds = request.DailyLimitSeconds;
        rule.IdleThresholdSeconds = Math.Clamp(request.IdleThresholdSeconds, 30, 3_600);
        rule.ScheduleJson = RuleSnapshotFactory.SerializeSchedule(request.Schedule);
        rule.ControlledUserSid = selectedUser?.Sid;
        rule.ControlledUserName = selectedUser?.AccountName;
        await MarkRulesChangedAsync(rule, cancellationToken);
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }

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
        await MarkRulesChangedAsync(rule, cancellationToken, "BlockPc");
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }

    [HttpPost("{deviceId:guid}/unblock")]
    public async Task<IActionResult> Unblock(Guid deviceId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);
        if (rule is null) return NotFound();
        rule.ManuallyBlocked = false;
        rule.ManualBlockUntilUtc = null;
        await MarkRulesChangedAsync(rule, cancellationToken, "UnblockPc");
        return Ok(await snapshots.CreateAsync(deviceId, cancellationToken));
    }

    private async Task MarkRulesChangedAsync(
        DeviceRule rule,
        CancellationToken cancellationToken,
        string commandType = "RulesChanged")
    {
        rule.Revision++;
        rule.UpdatedAtUtc = timeProvider.GetUtcNow();
        var command = new DeviceCommand { DeviceId = rule.DeviceId, Type = commandType };
        dbContext.DeviceCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.Group(DeviceHub.GroupName(rule.DeviceId))
            .SendAsync("Command", new { command.Id, command.Type }, cancellationToken);
    }
}
