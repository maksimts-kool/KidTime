using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Server;
using KidTime.ControlService.Sessions;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Ipc;

public sealed class NamedPipeHost(
    EnforcementCoordinator coordinator,
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
                var sample = await PipeProtocol.ReadAsync<SessionUsageSample>(pipe, stoppingToken);
                var response = await coordinator.HandleSampleAsync(sample, stoppingToken);
                var status = await coordinator.GetUserStatusAsync(runtimeStatus.Snapshot, stoppingToken);
                response = response with { Status = status };
                await PipeProtocol.WriteAsync(pipe, response, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                logger.LogWarning(exception, "Invalid or interrupted SessionAgent IPC exchange.");
            }
        }
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
