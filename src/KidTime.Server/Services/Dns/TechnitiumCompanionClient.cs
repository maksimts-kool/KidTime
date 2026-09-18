using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KidTime.Server.Services.Dns;

/// <summary>
/// Reads the Technitium DNS Companion. It only ever reads: every call here is a GET, and the one
/// POST is the login that gets a session to make them with. The parent changes their filtering in
/// the companion's own console, which is where the panel's button sends them - KidTime holding
/// write access to a DNS server would be a second place the same setting lives and a second way
/// for it to be wrong.
///
/// The companion authenticates with a session cookie rather than a header token, so one is kept
/// and renewed when it is refused.
/// </summary>
public sealed class TechnitiumCompanionClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DnsFilteringOptions _options;
    private readonly ILogger<TechnitiumCompanionClient> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private bool _signedIn;

    public TechnitiumCompanionClient(DnsFilteringOptions options, ILogger<TechnitiumCompanionClient> logger)
    {
        _options = options;
        _logger = logger;
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateCertificate;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri((_options.ApiUrl ?? "https://localhost:3443").TrimEnd('/') + "/api/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    /// <summary>
    /// Ordinary chain and hostname validation first, the configured pin only when that fails -
    /// the same order the Windows agent applies to this server, so a companion behind a proxy
    /// with a real certificate keeps working across renewals while a self-signed one still needs
    /// to be named.
    /// </summary>
    private bool ValidateCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (_options.AllowInvalidCertificate) return true;
        if (certificate is null || string.IsNullOrWhiteSpace(_options.PinnedCertificateSha256)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        var expected = _options.PinnedCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(expected.ToUpperInvariant()));
    }

    public async Task<CompanionState> ReadAsync(CancellationToken cancellationToken)
    {
        var blocking = await GetAsync<AdvancedBlockingResponse>(
            $"advanced-blocking/{Uri.EscapeDataString(_options.NodeId)}", cancellationToken);
        var schedules = await GetAsync<List<ScheduleRule>>("nodes/dns-schedules/rules", cancellationToken)
                        ?? [];
        var groups = await GetAsync<List<DomainGroupSummary>>("domain-groups", cancellationToken) ?? [];
        var details = new List<DomainGroupDetail>();
        foreach (var group in groups.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Take(50))
        {
            var detail = await GetAsync<DomainGroupDetail>(
                $"domain-groups/{Uri.EscapeDataString(group.Id!)}", cancellationToken);
            if (detail is not null) details.Add(detail);
        }

        return new CompanionState(blocking?.Config, schedules, details);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        await EnsureSignedInAsync(cancellationToken);
        var response = await _http.GetAsync(path, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // The session lasts eight hours and the server outlives it. One silent renewal is the
            // ordinary case, not a failure worth telling the parent about.
            _signedIn = false;
            await EnsureSignedInAsync(cancellationToken);
            response = await _http.GetAsync(path, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "The DNS companion answered {Status} for {Path}.", (int)response.StatusCode, path);
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (_signedIn) return;
        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (_signedIn) return;
            var response = await _http.PostAsJsonAsync(
                "auth/login",
                new { username = _options.Username, password = _options.Password },
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Never the body: it echoes the node's own session token.
                throw new HttpRequestException(
                    $"The DNS companion rejected the configured credentials (HTTP {(int)response.StatusCode}).");
            }

            _signedIn = true;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _loginGate.Dispose();
    }

    /// <summary>Everything one read of the companion returns, before it means anything.</summary>
    public sealed record CompanionState(
        AdvancedBlockingConfig? Blocking,
        List<ScheduleRule> Schedules,
        List<DomainGroupDetail> DomainGroups);

    public sealed record AdvancedBlockingResponse(AdvancedBlockingConfig? Config);

    public sealed record AdvancedBlockingConfig(
        bool EnableBlocking,
        Dictionary<string, string>? NetworkGroupMap,
        List<AdvancedBlockingGroup>? Groups);

    public sealed record AdvancedBlockingGroup(
        string? Name,
        bool EnableBlocking,
        List<string>? Blocked,
        List<string>? BlockListUrls,
        List<string>? AdblockListUrls,
        List<string>? RegexBlockListUrls);

    public sealed record ScheduleRule(
        string? Id,
        string? Name,
        bool Enabled,
        string? Action,
        List<string>? AdvancedBlockingGroupNames,
        List<string>? DomainGroupNames,
        List<string>? DomainEntries,
        List<int>? DaysOfWeek,
        string? StartTime,
        string? EndTime,
        string? Timezone);

    public sealed record DomainGroupSummary(string? Id, string? Name);

    public sealed record DomainGroupDetail(
        string? Id,
        string? Name,
        List<DomainGroupEntry>? Entries,
        List<DomainGroupBinding>? Bindings);

    public sealed record DomainGroupEntry(string? Value);

    public sealed record DomainGroupBinding(string? AdvancedBlockingGroupName, string? Action);
}
