using KidTime.Domain.Contracts;
using Microsoft.AspNetCore.SignalR.Client;

namespace KidTime.ControlService.Server;

public sealed class RealtimeCommandClient(
    AgentApiClient api,
    SyncTrigger trigger,
    ILogger<RealtimeCommandClient> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var credential = api.Credential;
            if (credential is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            await using var connection = new HubConnectionBuilder()
                .WithUrl(api.Options.ServerUrl.TrimEnd('/') + "/hubs/device", options =>
                {
                    options.Headers["X-Device-Id"] = credential.DeviceId.ToString();
                    options.Headers["X-Device-Token"] = credential.Token;
                    options.HttpMessageHandlerFactory = _ => api.CreateSignalRHandler();
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
                .Build();
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.On<object>("Command", command =>
            {
                logger.LogInformation("Realtime command received: {Command}.", command);
                trigger.Signal();
            });
            connection.Closed += exception =>
            {
                if (exception is not null)
                    logger.LogWarning(exception, "SignalR reconnect attempts were exhausted.");
                closed.TrySetResult(true);
                return Task.CompletedTask;
            };
            try
            {
                await connection.StartAsync(stoppingToken);
                logger.LogInformation("SignalR connected.");
                await closed.Task.WaitAsync(stoppingToken);
                logger.LogWarning("SignalR connection closed; rebuilding it while periodic synchronization remains active.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SignalR disconnected; periodic synchronization remains active.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }
}
