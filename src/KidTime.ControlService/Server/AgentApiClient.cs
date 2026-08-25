using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Removal;
using KidTime.Domain.Contracts;
using Microsoft.Extensions.Options;

namespace KidTime.ControlService.Server;

public sealed class AgentApiClient : IParentDeviceRemovalClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly CredentialStore _credentialStore;
    private readonly AgentOptions _options;
    private readonly HttpClient _client;

    public AgentApiClient(CredentialStore credentialStore, IOptions<AgentOptions> options)
    {
        _credentialStore = credentialStore;
        _options = options.Value;
        var serverUri = new Uri(_options.ServerUrl);
        if (serverUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Agent:ServerUrl must use HTTPS.");
        _client = new HttpClient(CertificateValidation.CreateHandler(_options))
        {
            BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public DeviceCredential? Credential => _credentialStore.Load();
    public AgentOptions Options => _options;

    public async Task<AgentSyncResponse> SyncAsync(CancellationToken cancellationToken) =>
        await SendAsync<AgentSyncResponse>(HttpMethod.Get, "api/agent/sync", null, cancellationToken);

    public async Task HeartbeatAsync(DeviceHeartbeatRequest heartbeat, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, "api/agent/heartbeat", heartbeat, cancellationToken);

    public async Task UploadUsageAsync(UsageBatchRequest batch, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, "api/agent/usage", batch, cancellationToken);

    public async Task UploadDiagnosticsAsync(DiagnosticReportBatch batch, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, "api/agent/diagnostics", batch, cancellationToken);

    public async Task UploadApplicationAsync(DiscoveredApplicationRequest application, CancellationToken cancellationToken) =>
        await SendAsync<JsonElement>(HttpMethod.Post, "api/agent/applications", application, cancellationToken);

    public async Task AcknowledgeCommandAsync(Guid commandId, CancellationToken cancellationToken) =>
        await SendNoContentAsync(HttpMethod.Post, $"api/agent/commands/{commandId}/ack", null, cancellationToken);

    public async Task<AgentUpdateManifest?> GetUpdateAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/agent/update", null);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentUpdateManifest>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException("Empty agent update manifest.");
    }

    public async Task DownloadUpdateAsync(string version, string destination, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/agent/update/package/{Uri.EscapeDataString(version)}", null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    public HttpClientHandler CreateSignalRHandler() => CertificateValidation.CreateHandler(_options);

    public async Task<bool> RemoveDeviceWithParentCredentialsAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var credential = Credential ?? throw new InvalidOperationException("This agent has not been enrolled.");
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new ParentLoginRequest(email, password), options: JsonOptions)
        };
        using var loginResponse = await _client.SendAsync(loginRequest, cancellationToken);
        if (loginResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<ParentLoginResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Parent authentication returned an empty response.");
        if (string.IsNullOrWhiteSpace(login.Token))
            throw new InvalidDataException("Parent authentication did not return a session token.");

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"api/devices/{credential.DeviceId}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        using var deleteResponse = await _client.SendAsync(deleteRequest, cancellationToken);
        if (deleteResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            deleteResponse.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Empty response from {path}.");
    }

    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var credential = Credential ?? throw new InvalidOperationException("This agent has not been enrolled.");
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Device-Id", credential.DeviceId.ToString());
        request.Headers.Add("X-Device-Token", credential.Token);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return request;
    }

    private sealed record ParentLoginRequest(string Email, string Password);
    private sealed record ParentLoginResponse(string Token);
}
