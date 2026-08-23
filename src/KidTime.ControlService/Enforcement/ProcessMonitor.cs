using System.Diagnostics;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Applications;
using KidTime.ControlService.Sessions;

namespace KidTime.ControlService.Enforcement;

public sealed class ProcessMonitor(
    ApplicationInspector inspector,
    LocalStore store,
    EnforcementCoordinator coordinator,
    ILogger<ProcessMonitor> logger) : BackgroundService
{
    private readonly Dictionary<int, TrackedApplication> _tracked = [];
    private readonly HashSet<int> _ignored = [];
    private readonly Dictionary<string, BlockLease> _blockLeases = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!WindowsSession.IsActiveUserControlled(coordinator.Rules))
            {
                _tracked.Clear();
                _ignored.Clear();
                _blockLeases.Clear();
                continue;
            }

            var activeSessionId = checked((int)WindowsSession.ActiveSessionId);
            var processes = Process.GetProcesses();
            var current = new HashSet<int>();
            var runningApplications = new Dictionary<string, RunningApplication>(StringComparer.Ordinal);
            foreach (var process in processes)
            {
                var retained = false;
                try
                {
                    if (process.SessionId != activeSessionId) continue;
                    current.Add(process.Id);
                    if (_ignored.Contains(process.Id)) continue;
                    if (!_tracked.TryGetValue(process.Id, out var tracked))
                    {
                        var inspected = inspector.Inspect(process);
                        if (inspected is null || !ApplicationCatalogPolicy.IsUserManageable(inspected))
                        {
                            _ignored.Add(process.Id);
                            continue;
                        }
                        var descriptor = ApplicationCatalogPolicy.NormalizeForCatalog(inspected);
                        var identity = ApplicationIdentity.CreateKey(descriptor);
                        tracked = new TrackedApplication(identity, descriptor.DisplayName);
                        _tracked[process.Id] = tracked;
                        await store.UpsertApplicationAsync(
                            identity, descriptor, stoppingToken, forceSynchronization: true);
                        logger.LogInformation("Application discovered: {Application} ({Path}).", descriptor.DisplayName, descriptor.ExecutablePath);
                    }
                    if (!runningApplications.TryGetValue(tracked.IdentityKey, out var running))
                        runningApplications[tracked.IdentityKey] = running = new RunningApplication(tracked.DisplayName, []);
                    running.Processes.Add(process);
                    retained = true;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    logger.LogDebug(exception, "Process inspection failed for {ProcessId}.", process.Id);
                }
                finally { if (!retained) process.Dispose(); }
            }

            foreach (var processId in _tracked.Keys.Where(processId => !current.Contains(processId)).ToList())
                _tracked.Remove(processId);
            _ignored.RemoveWhere(processId => !current.Contains(processId));
            foreach (var identity in _blockLeases.Keys.Where(identity => !runningApplications.ContainsKey(identity)).ToList())
            {
                _blockLeases.Remove(identity);
                coordinator.DismissApplicationClosing(identity);
            }

            foreach (var (identity, running) in runningApplications)
            {
                try
                {
                    var status = await coordinator.EvaluateApplicationStatusAsync(identity, stoppingToken);
                    if (status is null || status.Decision.IsAllowed)
                    {
                        if (_blockLeases.Remove(identity)) coordinator.DismissApplicationClosing(identity);
                        continue;
                    }

                    if (!_blockLeases.TryGetValue(identity, out var lease)
                        || !string.Equals(lease.EpisodeKey, status.EpisodeKey, StringComparison.Ordinal))
                    {
                        var firstBlockedLaunch = await store.TryConsumeFirstApplicationBlockGraceAsync(
                            identity, status.EpisodeKey, stoppingToken);
                        var seconds = firstBlockedLaunch ? 60 : 20;
                        lease = new BlockLease(
                            status.EpisodeKey,
                            Stopwatch.GetTimestamp() + (long)(seconds * (double)Stopwatch.Frequency),
                            ClosingIssued: false);
                        _blockLeases[identity] = lease;
                        coordinator.NotifyApplicationClosing(identity, status.DisplayName, status.Decision, seconds);
                    }

                    if (!lease.ClosingIssued && Stopwatch.GetTimestamp() >= lease.CloseAtTimestamp)
                    {
                        foreach (var process in running.Processes)
                        {
                            try { process.Kill(entireProcessTree: true); }
                            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                            {
                                logger.LogWarning(exception, "Could not close blocked application process {ProcessId}.", process.Id);
                            }
                        }
                        _blockLeases[identity] = lease with { ClosingIssued = true };
                        logger.LogWarning("Blocked application {Application} closed after its save-work period.", status.DisplayName);
                    }
                }
                finally
                {
                    foreach (var process in running.Processes) process.Dispose();
                }
            }
        }
    }

    private sealed record TrackedApplication(string IdentityKey, string DisplayName);
    private sealed record RunningApplication(string DisplayName, List<Process> Processes);
    private sealed record BlockLease(string EpisodeKey, long CloseAtTimestamp, bool ClosingIssued);
}
