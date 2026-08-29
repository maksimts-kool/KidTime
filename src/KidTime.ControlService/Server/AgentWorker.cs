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
    TimeExtensionService extensions,
    InstalledApplicationDiscovery discovery,
    TrustedClock clock,
    SyncTrigger trigger,
    WindowsAccountProvider accounts,
    AgentUpdateState updateState,
    AgentRuntimeStatus runtimeStatus,
    DiagnosticReporter diagnostics,
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
        try
        {
            await SynchronizationLoopAsync(stoppingToken);
        }
        finally
        {
            // Counted seconds are buffered in memory between flushes, so a service that is
            // stopping - a restart, an automatic update, a shutdown - writes them out first.
            await coordinator.FlushUsageAsync(CancellationToken.None);
        }
    }

    private async Task SynchronizationLoopAsync(CancellationToken stoppingToken)
    {
        var nextSync = DateTimeOffset.MinValue;
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (api.Credential is null)
            {
                runtimeStatus.MarkNotEnrolled();
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
                    runtimeStatus.MarkSynchronizationSucceeded();
                    nextSync = now.AddSeconds(Math.Clamp(api.Options.SyncIntervalSeconds, 15, 3_600));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
                {
                    runtimeStatus.MarkSynchronizationFailed(exception.Message);
                    logger.LogWarning(exception, "Synchronization failed; cached rules remain active and usage stays queued locally.");
                    nextSync = now.AddSeconds(20);
                }
            }

            if (now >= nextHeartbeat)
            {
                try
                {
                    var rules = coordinator.Rules;
                    var today = await coordinator.GetPcStatusAsync(stoppingToken);
                    var update = updateState.Snapshot;
                    await api.HeartbeatAsync(new DeviceHeartbeatRequest(
                        WindowsSession.GetActiveUserName(),
                        coordinator.ForegroundName,
                        coordinator.ForegroundIdentity,
                        today.TodayActiveSeconds,
                        clock.GetUtcNow(),
                        rules.Revision,
                        await accounts.GetAccountsAsync(stoppingToken),
                        updateState.CurrentVersion,
                        update.Status,
                        update.Error,
                        update.CheckedAtUtc), stoppingToken);
                    runtimeStatus.MarkContactSucceeded();
                    nextHeartbeat = now.AddSeconds(Math.Clamp(api.Options.HeartbeatIntervalSeconds, 10, 600));
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    runtimeStatus.MarkDisconnected();
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
        await UploadDiagnosticsAsync(cancellationToken);
        // Buffered seconds have to reach the database before the batch that uploads them is cut.
        await coordinator.FlushUsageAsync(cancellationToken);
        foreach (var application in await store.GetApplicationsToSyncAsync(cancellationToken))
        {
            await api.UploadApplicationAsync(application, cancellationToken);
            await store.MarkApplicationSynchronizedAsync(application.IdentityKey, cancellationToken);
        }

        await UploadTimeExtensionsAsync(cancellationToken);

        await store.PrepareUsageBatchAsync(cancellationToken);
        foreach (var batch in await store.GetPendingBatchesAsync(cancellationToken))
        {
            await api.UploadUsageAsync(batch, cancellationToken);
            await store.CompleteBatchAsync(batch.BatchId, cancellationToken);
        }

        var sync = await api.SyncAsync(cancellationToken);
        clock.Synchronize(sync.ServerUtcNow);
        await store.SaveRulesAsync(sync.Rules, cancellationToken);
        // The granted minutes are already inside the snapshot the coordinator has just been
        // given, so this only decides what the child is told - and it runs after UpdateRules so a
        // "30 minutes added" message can never arrive before the minutes themselves.
        coordinator.UpdateRules(sync.Rules);
        await AnnounceTimeExtensionDecisionsAsync(sync.TimeExtensions ?? [], cancellationToken);
        foreach (var command in sync.Commands)
            await api.AcknowledgeCommandAsync(command.Id, cancellationToken);
        logger.LogInformation("Synchronization completed at rule revision {Revision}.", sync.Rules.Revision);
    }

    /// <summary>
    /// Sends the requests a child has made and have not reached the server yet. A request is
    /// marked uploaded only after the server has taken it, and its id was minted on this PC, so a
    /// retry after an uncertain response cannot ask the parent the same question twice.
    /// </summary>
    private async Task UploadTimeExtensionsAsync(CancellationToken cancellationToken)
    {
        var pending = await extensions.GetPendingUploadsAsync(cancellationToken);
        if (pending.Count == 0) return;
        await api.UploadTimeExtensionsAsync(
            new TimeExtensionBatch(pending.Select(item => new TimeExtensionRequest(
                item.RequestId,
                item.RequestedAtUtc,
                item.LocalDate,
                item.RequestedMinutes,
                item.ApplicationIdentityKey,
                item.DisplayName)).ToList()),
            cancellationToken);
        foreach (var item in pending)
            await extensions.MarkUploadedAsync(item.RequestId, cancellationToken);
    }

    /// <summary>
    /// Tells the child, once, what their parent decided. The record is marked announced only
    /// after the message is queued, so an answer survives a service restart; the queue itself is
    /// what carries it to the tray agent on the next pipe exchange.
    /// </summary>
    private async Task AnnounceTimeExtensionDecisionsAsync(
        IReadOnlyList<TimeExtensionDecision> decisions,
        CancellationToken cancellationToken)
    {
        await extensions.ApplyDecisionsAsync(decisions, cancellationToken);
        foreach (var answered in await extensions.GetAnnouncementsAsync(cancellationToken))
        {
            coordinator.NotifyTimeExtensionDecision(answered);
            await extensions.MarkAnnouncedAsync(answered.RequestId, cancellationToken);
        }

        await extensions.TrimAsync(
            RuleEvaluator.GetLocalDate(clock.GetUtcNow(), coordinator.Rules.TimeZoneId),
            cancellationToken);
    }

    /// <summary>
    /// Faults are uploaded before rules and usage so a parent still learns about a failing PC
    /// even when a later step of the same synchronization is what keeps failing.
    /// </summary>
    private async Task UploadDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await diagnostics.FlushToStoreAsync(store, cancellationToken);
        var pending = await store.GetPendingDiagnosticsAsync(cancellationToken);
        if (pending.Count == 0) return;
        await api.UploadDiagnosticsAsync(new DiagnosticReportBatch(pending), cancellationToken);
        await store.CompleteDiagnosticsAsync(pending.Select(report => report.ReportId), cancellationToken);
    }
}
