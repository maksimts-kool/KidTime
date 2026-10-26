using KidTime.Server.Data;
using KidTime.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KidTime.Server.Services;

/// <summary>
/// The one way a parent's change reaches a PC: bump the device's rule revision, queue a command,
/// save, and push the command over the hub. The agent detects change by revision and the hub only
/// nudges it, so the two must never drift apart - which is why every controller that changes a
/// rule goes through here rather than repeating the four steps.
/// </summary>
public sealed class RuleChangePublisher(
    KidTimeDbContext dbContext,
    IHubContext<DeviceHub> hubContext,
    TimeProvider timeProvider)
{
    public const string RulesChanged = "RulesChanged";

    /// <summary>Saves every pending change in the context along with the bumped revision.</summary>
    public async Task PublishAsync(DeviceRule rule, CancellationToken cancellationToken, string commandType = RulesChanged)
    {
        rule.Revision++;
        rule.UpdatedAtUtc = timeProvider.GetUtcNow();
        var command = new DeviceCommand { DeviceId = rule.DeviceId, Type = commandType };
        dbContext.DeviceCommands.Add(command);
        await dbContext.SaveChangesAsync(cancellationToken);
        await hubContext.Clients.Group(DeviceHub.GroupName(rule.DeviceId))
            .SendAsync("Command", new { command.Id, command.Type }, cancellationToken);
    }

    public async Task PublishAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var rule = await dbContext.DeviceRules.SingleAsync(x => x.DeviceId == deviceId, cancellationToken);
        await PublishAsync(rule, cancellationToken);
    }
}
