using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Sessions;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Server;

public sealed class AgentWorker(
    LocalStore store,
    AgentApiClient api,
    EnforcementCoordinator coordinator,
    InstalledApplicationDiscovery discovery,
    TrustedClock clock,
    SyncTrigger trigger,
    WindowsAccountProvider accounts,
    AgentUpdateState updateState,
    ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.InitializeAsync(stoppingToken);
        if (await store.LoadRulesAsync(stoppingToken) is { } cachedRules)
        {
            coordinator.UpdateRules(cachedRules);
            logger.LogInformation("Loaded cached rule revision {Revision}; offline enforcement is active.", cachedRules.Revision);
        }
        else
        {
            logger.LogWarning("No cached rules exist yet; enroll and synchronize this device.");
        }

        await discovery.DiscoverAsync(stoppingToken);
        var nextSync = DateTimeOffset.MinValue;
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (api.Credential is null)
            {
                logger.LogWarning("Device is not enrolled. Run the enrollment command before starting the service.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextSync)
            {
                try
                {
                    await SynchronizeAsync(stoppingToken);
                    nextSync = now.AddSeconds(Math.Clamp(api.Options.SyncIntervalSeconds, 15, 3_600));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
                {
                    logger.LogWarning(exception, "Synchronization failed; cached rules remain active and usage stays queued locally.");
                    nextSync = now.AddSeconds(20);
                }
            }

            if (now >= nextHeartbeat)
            {
                try
                {
                    var rules = coordinator.Rules;
                    var localDate = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), rules.TimeZoneId);
                    var today = await store.GetUsageAsync(localDate, null, stoppingToken);
                    var update = updateState.Snapshot;
                    await api.HeartbeatAsync(new DeviceHeartbeatRequest(
                        WindowsSession.GetActiveUserName(),
                        coordinator.ForegroundName,
                        coordinator.ForegroundIdentity,
                        today,
                        clock.GetUtcNow(),
                        rules.Revision,
                        await accounts.GetAccountsAsync(stoppingToken),
                        updateState.CurrentVersion,
                        update.Status,
                        update.Error,
                        update.CheckedAtUtc), stoppingToken);
                    nextHeartbeat = now.AddSeconds(Math.Clamp(api.Options.HeartbeatIntervalSeconds, 10, 600));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    logger.LogInformation("Server disconnected: {Message}", exception.Message);
                    nextHeartbeat = now.AddSeconds(15);
                }
            }

            if (await trigger.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken))
                nextSync = DateTimeOffset.MinValue;
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        foreach (var application in await store.GetApplicationsToSyncAsync(cancellationToken))
        {
            await api.UploadApplicationAsync(application, cancellationToken);
            await store.MarkApplicationSynchronizedAsync(application.IdentityKey, cancellationToken);
        }

        await store.PrepareUsageBatchAsync(cancellationToken);
        foreach (var batch in await store.GetPendingBatchesAsync(cancellationToken))
        {
            await api.UploadUsageAsync(batch, cancellationToken);
            await store.CompleteBatchAsync(batch.BatchId, cancellationToken);
        }

        var sync = await api.SyncAsync(cancellationToken);
        clock.Synchronize(sync.ServerUtcNow);
        await store.SaveRulesAsync(sync.Rules, cancellationToken);
        coordinator.UpdateRules(sync.Rules);
        foreach (var command in sync.Commands)
            await api.AcknowledgeCommandAsync(command.Id, cancellationToken);
        logger.LogInformation("Synchronization completed at rule revision {Revision}.", sync.Rules.Revision);
    }
}
