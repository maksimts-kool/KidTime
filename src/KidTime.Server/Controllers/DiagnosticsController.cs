using KidTime.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Controllers;

/// <summary>
/// The parent-facing view of faults reported by enrolled PCs. Once a device is handed to a
/// child it is usually unreachable, so this endpoint is how its crashes and errors surface.
/// </summary>
[ApiController]
[Authorize]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(
    KidTimeDbContext dbContext,
    TimeProvider timeProvider) : ControllerBase
{
    private const int MaximumReturned = 200;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? deviceId,
        [FromQuery] bool includeResolved,
        CancellationToken cancellationToken)
    {
        var query = dbContext.DeviceDiagnosticEvents.AsNoTracking()
            .Include(item => item.Device)
            .AsQueryable();
        if (deviceId is { } id) query = query.Where(item => item.DeviceId == id);
        if (!includeResolved) query = query.Where(item => item.ResolvedAtUtc == null);

        var events = await query
            .OrderByDescending(item => item.LastOccurredAtUtc)
            .Take(MaximumReturned)
            .Select(item => new
            {
                item.Id,
                item.DeviceId,
                deviceName = item.Device.Name,
                item.Component,
                item.Severity,
                item.Message,
                item.ExceptionType,
                item.Detail,
                item.AgentVersion,
                item.OccurrenceCount,
                item.FirstOccurredAtUtc,
                item.LastOccurredAtUtc,
                item.ResolvedAtUtc
            })
            .ToListAsync(cancellationToken);
        return Ok(events);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var unresolved = await dbContext.DeviceDiagnosticEvents.AsNoTracking()
            .Where(item => item.ResolvedAtUtc == null)
            .GroupBy(item => item.Severity)
            .Select(group => new { severity = group.Key, count = group.Count() })
            .ToListAsync(cancellationToken);
        return Ok(new
        {
            unresolved = unresolved.Sum(entry => entry.count),
            bySeverity = unresolved
        });
    }

    [HttpPost("{eventId:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid eventId, CancellationToken cancellationToken)
    {
        var item = await dbContext.DeviceDiagnosticEvents.SingleOrDefaultAsync(
            entry => entry.Id == eventId, cancellationToken);
        if (item is null) return NotFound();
        item.ResolvedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("resolve")]
    public async Task<IActionResult> ResolveAll([FromQuery] Guid? deviceId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var query = dbContext.DeviceDiagnosticEvents.Where(item => item.ResolvedAtUtc == null);
        if (deviceId is { } id) query = query.Where(item => item.DeviceId == id);
        var resolved = await query.ExecuteUpdateAsync(
            setters => setters.SetProperty(item => item.ResolvedAtUtc, now),
            cancellationToken);
        return Ok(new { resolved });
    }

    [HttpDelete("{eventId:guid}")]
    public async Task<IActionResult> Delete(Guid eventId, CancellationToken cancellationToken)
    {
        var removed = await dbContext.DeviceDiagnosticEvents
            .Where(item => item.Id == eventId)
            .ExecuteDeleteAsync(cancellationToken);
        return removed == 0 ? NotFound() : NoContent();
    }
}
