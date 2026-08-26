using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Removal;
using KidTime.ControlService.Server;
using KidTime.ControlService.Sessions;
using KidTime.Domain.Contracts;
using KidTime.Domain.Localization;

namespace KidTime.ControlService.Ipc;

public sealed class NamedPipeHost(
    EnforcementCoordinator coordinator,
    DeviceRemovalService removalService,
    AgentRuntimeStatus runtimeStatus,
    SessionAgentSupervisor supervisor,
    DiagnosticReporter diagnostics,
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
        AcceptDiagnostics(request.Diagnostics);

        if (request is { UsageSample: { } sample, RemovalRequest: null })
        {
            var enforcement = await coordinator.HandleSampleAsync(sample, cancellationToken);
            // The screen-time window is the only consumer of the full snapshot, and building it
            // reads every application rule. The agent asks for it only while that window is open.
            var status = sample.StatusRequested
                ? await coordinator.GetUserStatusAsync(runtimeStatus.Snapshot, cancellationToken)
                : null;
            return new SessionAgentResponse(Enforcement: enforcement with { Status = status });
        }

        if (request is { UsageSample: null, RemovalRequest: { } removal })
        {
            var result = await removalService.AuthorizeAndScheduleAsync(
                removal,
                cancellationToken,
                AgentStrings.For(coordinator.Rules.Language));
            return new SessionAgentResponse(Removal: result);
        }

        if (request is { UsageSample: null, RemovalRequest: null, Diagnostics.Count: > 0 })
            return new SessionAgentResponse();

        throw new InvalidDataException("IPC request must contain exactly one supported operation.");
    }

    /// <summary>
    /// Fault reports are the third and last shape this pipe accepts. They are inert data: the
    /// component is stamped by the service rather than trusted from the message, every field is
    /// truncated, and the batch is bounded, so the unelevated agent cannot use this path to
    /// impersonate the service, flood the queue, or reach any privileged operation.
    /// </summary>
    private void AcceptDiagnostics(IReadOnlyList<DiagnosticReport>? reports)
    {
        if (reports is null) return;
        foreach (var report in reports.Take(DiagnosticReportPolicy.MaximumReportsPerBatch))
            diagnostics.Enqueue(DiagnosticReportPolicy.Normalize(report, DiagnosticComponents.SessionAgent));
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
