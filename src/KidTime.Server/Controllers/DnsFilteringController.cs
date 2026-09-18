using KidTime.Domain.Contracts;
using KidTime.Server.Services.Dns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KidTime.Server.Controllers;

/// <summary>
/// What the panel needs to hand the parent over to their DNS server: whether one is configured,
/// whether it is filtering, and the address of its own console. KidTime does not edit any of it -
/// the console is where filtering is set up, and this endpoint exists so the panel can say
/// whether it is worth opening rather than offering a link into nothing.
/// </summary>
[ApiController]
[Authorize]
[Route("api/dns")]
public sealed class DnsFilteringController(DnsFilteringService filtering) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var snapshot = await filtering.GetAsync(cancellationToken);
        return Ok(new
        {
            consoleUrl = filtering.ConsoleUrl,
            state = snapshot.State.ToString(),
            groupName = snapshot.GroupName,
            retrievedAtUtc = snapshot.RetrievedAtUtc,
            isStale = snapshot.IsStale,
            categories = snapshot.Categories.Select(item => new
            {
                kind = item.Kind.ToString(),
                listCount = item.ListCount
            }),
            siteGroups = snapshot.SiteGroups.Select(item => new
            {
                name = item.Name,
                siteCount = item.SiteCount,
                isBlockedNow = item.IsBlockedNow,
                changesAtUtc = item.ChangesAtUtc,
                windows = item.Windows.Select(window => new
                {
                    startTime = window.StartTime,
                    endTime = window.EndTime,
                    days = window.Days.Select(day => (int)day)
                })
            })
        });
    }
}
