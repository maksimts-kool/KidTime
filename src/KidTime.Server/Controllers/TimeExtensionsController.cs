using System.Security.Claims;
using KidTime.Domain.Contracts;
using KidTime.Server.Data;
using KidTime.Server.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

/// <summary>
/// The parent's side of extra time: what has been asked for, and what to do about it.
///
/// A decision is a rule change like any other - it bumps the device's rule revision, queues a
/// command, and is pushed over the hub - because that is the one path the controlled PC already
/// watches. Approving grants minutes for the local date the child asked on, so a request that
/// sat unanswered until the next day grants nothing even if it is approved late.
/// </summary>
[ApiController]
[Authorize]
[Route("api/time-extensions")]
public sealed class TimeExtensionsController(
    KidTimeDbContext dbContext,
    IHubContext<DeviceHub> hubContext,
    TimeProvider timeProvider) : ControllerBase
{
    public sealed record DecisionRequest(bool Approved, int? Minutes);

    [HttpGet]
    public async Task<IActionResult> List(Guid? deviceId, bool includeDecided, CancellationToken cancellationToken)
    {
        var query = dbContext.TimeExtensions.AsNoTracking().Include(item => item.Device).AsQueryable();
        if (deviceId is not null) query = query.Where(item => item.DeviceId == deviceId);
        if (!includeDecided) query = query.Where(item => item.Status == TimeExtensionStatuses.Pending);
        var items = await query
            .OrderByDescending(item => item.RequestedAtUtc)
            .Take(200)
            .Select(item => new
            {
                item.Id,
                item.DeviceId,
                deviceName = item.Device.Name,
                item.DisplayName,
                item.ApplicationIdentityKey,
                item.DeviceApplicationId,
                isPc = item.ApplicationIdentityKey == null,
                item.LocalDate,
                item.RequestedMinutes,
                item.GrantedMinutes,
                item.Status,
                item.RequestedAtUtc,
                item.DecidedAtUtc
            })
            .ToListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpGet("pending/count")]
    public async Task<IActionResult> PendingCount(CancellationToken cancellationToken) =>
        Ok(new
        {
            count = await dbContext.TimeExtensions
                .CountAsync(item => item.Status == TimeExtensionStatuses.Pending, cancellationToken)
        });

    [HttpPost("{requestId:guid}/decision")]
    public async Task<IActionResult> Decide(
        Guid requestId,
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        var granted = request.Approved ? request.Minutes ?? 0 : 0;
        if (request.Approved && !TimeExtensionPolicy.IsAllowedGrant(granted))
            return BadRequest($"Granted time must be 1-{TimeExtensionPolicy.MaximumGrantedMinutes} minutes.");

        var item = await dbContext.TimeExtensions.SingleOrDefaultAsync(
            row => row.Id == requestId, cancellationToken);
        if (item is null) return NotFound();
        if (item.Status != TimeExtensionStatuses.Pending) return Conflict("This request has already been decided.");

        var now = timeProvider.GetUtcNow();
        item.Status = request.Approved ? TimeExtensionStatuses.Approved : TimeExtensionStatuses.Denied;
        item.GrantedMinutes = granted;
        item.DecidedAtUtc = now;
        item.DecidedByParentUserId = ParentUserId;

        // A denial bumps the revision too. The extra minutes are not the only thing that has to
        // reach the PC quickly - so does the answer, because a child left staring at "waiting for
        // your parent" has been told nothing at all.
        var deviceRule = await dbContext.DeviceRules.SingleAsync(
            rule => rule.DeviceId == item.DeviceId, cancellationToken);
        deviceRule.Revision++;
        deviceRule.UpdatedAtUtc = now;
        var command = new DeviceCommand { DeviceId = item.DeviceId, Type = "RulesChanged" };
        dbContext.DeviceCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.Group(DeviceHub.GroupName(item.DeviceId))
            .SendAsync("Command", new { command.Id, command.Type }, cancellationToken);
        return NoContent();
    }

    private Guid ParentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
