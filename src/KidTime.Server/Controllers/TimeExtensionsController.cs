using System.Security.Claims;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using KidTime.Server.Data;
using KidTime.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    RuleChangePublisher ruleChanges,
    TimeProvider timeProvider) : ControllerBase
{
    public sealed record DecisionRequest(bool Approved, int? Minutes);

    /// <summary>Extra time a parent gives unasked: to the PC, or to one application on it.</summary>
    public sealed record GrantRequest(Guid DeviceId, Guid? DeviceApplicationId, int Minutes);

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
                item.DecidedAtUtc,
                item.InitiatedByParent
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
        await ruleChanges.PublishAsync(item.DeviceId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Gives extra time without waiting to be asked - an afternoon that ran long for a good reason,
    /// a game that needs ten more minutes to save. It is recorded as a request the child never
    /// made, already approved, so it reaches the PC through exactly the path an approval takes:
    /// minutes added to today's limit, and a window over a block or a closed schedule counted from
    /// this moment. The child is told once, the same way.
    /// </summary>
    [HttpPost("grant")]
    public async Task<IActionResult> Grant(GrantRequest request, CancellationToken cancellationToken)
    {
        if (!TimeExtensionPolicy.IsAllowedGrant(request.Minutes))
            return BadRequest($"Granted time must be 1-{TimeExtensionPolicy.MaximumGrantedMinutes} minutes.");
        var device = await dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == request.DeviceId, cancellationToken);
        if (device is null) return NotFound();

        string? identityKey = null;
        var displayName = "PC";
        if (request.DeviceApplicationId is { } applicationId)
        {
            var application = await dbContext.DeviceApplications.AsNoTracking()
                .Include(item => item.Application)
                .SingleOrDefaultAsync(item => item.Id == applicationId && item.DeviceId == request.DeviceId,
                    cancellationToken);
            if (application is null) return NotFound();
            identityKey = application.Application.IdentityKey;
            displayName = ApplicationCatalogPolicy.GetFriendlyDisplayName(application.ToDescriptor());
        }

        var now = timeProvider.GetUtcNow();
        dbContext.TimeExtensions.Add(new TimeExtension
        {
            Id = Guid.NewGuid(),
            DeviceId = request.DeviceId,
            DeviceApplicationId = request.DeviceApplicationId,
            ApplicationIdentityKey = identityKey,
            DisplayName = displayName,
            LocalDate = RuleEvaluator.GetLocalDate(now, device.TimeZoneId),
            RequestedMinutes = 0,
            GrantedMinutes = request.Minutes,
            Status = TimeExtensionStatuses.Approved,
            RequestedAtUtc = now,
            ReceivedAtUtc = now,
            DecidedAtUtc = now,
            DecidedByParentUserId = ParentUserId,
            InitiatedByParent = true
        });
        await ruleChanges.PublishAsync(request.DeviceId, cancellationToken);
        return NoContent();
    }


    /// <summary>
    /// Takes back extra time that was given - a grant approved by mistake, or one the child no
    /// longer deserves. The row goes, so the next snapshot carries neither its minutes nor the
    /// window it held a block open, and it no longer counts against the child's daily requests.
    /// Only a grant can be taken back: a refusal locks the scope on the PC itself, so deleting one
    /// here would tell the parent the child may ask again while the PC still says no.
    /// </summary>
    [HttpDelete("{requestId:guid}")]
    public async Task<IActionResult> Revoke(Guid requestId, CancellationToken cancellationToken)
    {
        var item = await dbContext.TimeExtensions.SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken);
        if (item is null) return NotFound();
        if (item.Status != TimeExtensionStatuses.Approved) return Conflict("Only extra time that was given can be taken back.");
        dbContext.TimeExtensions.Remove(item);
        await ruleChanges.PublishAsync(item.DeviceId, cancellationToken);
        return NoContent();
    }

    private Guid ParentUserId =>Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
