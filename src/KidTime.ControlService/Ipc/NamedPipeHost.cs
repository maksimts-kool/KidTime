using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Removal;
using KidTime.ControlService.Server;
using KidTime.ControlService.Sessions;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Ipc;

public sealed class NamedPipeHost(
    EnforcementCoordinator coordinator,
    DeviceRemovalService removalService,
    AgentRuntimeStatus runtimeStatus,
    SessionAgentSupervisor supervisor,
    ILogger<NamedPipeHost> logger) : BackgroundService
{
    public const string PipeName = "KidTime.ControlService.v1";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Secure SessionAgent named pipe started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId)
                    || !supervisor.IsTrustedAgentProcess(clientProcessId))
                {
                    logger.LogWarning("Rejected named-pipe client process {ClientProcessId}; expected supervised SessionAgent {ExpectedProcessId}.",
                        clientProcessId, supervisor.AgentProcessId);
                    continue;
                }
                var request = await PipeProtocol.ReadAsync<SessionAgentRequest>(pipe, stoppingToken);
                var response = await HandleRequestAsync(request, stoppingToken);
                await PipeProtocol.WriteAsync(pipe, response, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                logger.LogWarning(exception, "Invalid or interrupted SessionAgent IPC exchange.");
            }
        }
    }

    private async Task<SessionAgentResponse> HandleRequestAsync(
        SessionAgentRequest request,
        CancellationToken cancellationToken)
    {
        if (request is { UsageSample: { } sample, RemovalRequest: null })
        {
            var enforcement = await coordinator.HandleSampleAsync(sample, cancellationToken);
            var status = await coordinator.GetUserStatusAsync(runtimeStatus.Snapshot, cancellationToken);
            return new SessionAgentResponse(Enforcement: enforcement with { Status = status });
        }

        if (request is { UsageSample: null, RemovalRequest: { } removal })
        {
            var result = await removalService.AuthorizeAndScheduleAsync(removal, cancellationToken);
            return new SessionAgentResponse(Removal: result);
        }

        throw new InvalidDataException("IPC request must contain exactly one supported operation.");
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            64 * 1024,
            64 * 1024,
            security);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);
}
