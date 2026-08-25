using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

internal sealed class PipeClient
{
    private const int MaximumMessageBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnforcementState?> ExchangeAsync(
        SessionUsageSample sample,
        IReadOnlyList<DiagnosticReport>? diagnostics,
        CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(
            new SessionAgentRequest(UsageSample: sample, Diagnostics: diagnostics),
            cancellationToken);
        return response.Enforcement;
    }

    public async Task<DeviceRemovalResult> RequestRemovalAsync(
        ParentRemovalRequest request,
        CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(new SessionAgentRequest(RemovalRequest: request), cancellationToken);
        return response.Removal ?? throw new InvalidDataException("The service returned an empty removal response.");
    }

    private static async Task<SessionAgentResponse> ExchangeAsync(
        SessionAgentRequest request,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", "KidTime.ControlService.v1", PipeDirection.InOut,
            PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(2_000, cancellationToken);
        await WriteAsync(pipe, request, cancellationToken);
        return await ReadAsync<SessionAgentResponse>(pipe, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid IPC response length.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions) ?? throw new InvalidDataException("Empty IPC response.");
    }

    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var lengthBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
