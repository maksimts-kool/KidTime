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
/// and renewed when it is refused - and "refused" has two shapes here, only one of which is an
/// HTTP status. See <see cref="GetAsync{T}"/>.
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
        : this(options, logger, CreateHandler(options))
    {
    }

    /// <summary>
    /// The same client over a handler the caller supplies, so a test can answer as the companion
    /// does. What is worth testing here is what KidTime makes of the answers, and reaching a real
    /// DNS server to find out is not a test.
    /// </summary>
    internal TechnitiumCompanionClient(
        DnsFilteringOptions options,
        ILogger<TechnitiumCompanionClient> logger,
        HttpMessageHandler handler)
    {
        _options = options;
        _logger = logger;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri((options.ApiUrl ?? "https://localhost:3443").TrimEnd('/') + "/api/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private static SocketsHttpHandler CreateHandler(DnsFilteringOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) => ValidateCertificate(options, certificate, errors);
        return handler;
    }

    /// <summary>
    /// Ordinary chain and hostname validation first, the configured pin only when that fails -
    /// the same order the Windows agent applies to this server, so a companion behind a proxy
    /// with a real certificate keeps working across renewals while a self-signed one still needs
    /// to be named.
    /// </summary>
    private static bool ValidateCertificate(
        DnsFilteringOptions options,
        X509Certificate? certificate,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (options.AllowInvalidCertificate) return true;
        if (certificate is null || string.IsNullOrWhiteSpace(options.PinnedCertificateSha256)) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
        var expected = options.PinnedCertificateSha256.Replace(":", string.Empty, StringComparison.Ordinal).Trim();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(actual.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(expected.ToUpperInvariant()));
    }

    /// <summary>
    /// One complete read, or nothing. **A read that half succeeded is a failed read**, because the
    /// caller cannot tell a configuration it could not fetch from one that blocks nothing, and the
    /// second of those is a household being told its filter is off while the filter is running.
    /// Whatever cannot be read throws, and <see cref="DnsFilteringService"/> then keeps the answer
    /// before it.
    /// </summary>
    public async Task<CompanionState> ReadAsync(CancellationToken cancellationToken)
    {
        // The advanced-blocking configuration is the anchor of the whole read - it is what says
        // whether this household is filtered at all - and it is also the one the companion serves
        // out of the DNS node rather than out of itself, so it is where a stale node token shows.
        var blocking = await GetAsync<AdvancedBlockingResponse>(
            $"advanced-blocking/{Uri.EscapeDataString(_options.NodeId)}",
            cancellationToken,
            answer => answer?.Config is not null);
        if (blocking?.Config is null)
        {
            throw new DnsCompanionException(
                $"The DNS companion answered for node \"{_options.NodeId}\" without its Advanced Blocking "
                + "configuration, which is what it does when the DNS node has rejected the token it holds. "
                + "Signing in again did not mint a working one.");
        }

        var schedules = await GetAsync<List<ScheduleRule>>("nodes/dns-schedules/rules", cancellationToken)
                        ?? throw new DnsCompanionException("The DNS companion did not return its blocking schedules.");
        var groups = await GetAsync<List<DomainGroupSummary>>("domain-groups", cancellationToken)
                     ?? throw new DnsCompanionException("The DNS companion did not return its domain groups.");
        var details = new List<DomainGroupDetail>();
        foreach (var group in groups.Where(item => !string.IsNullOrWhiteSpace(item.Id)).Take(50))
        {
            var detail = await GetAsync<DomainGroupDetail>(
                $"domain-groups/{Uri.EscapeDataString(group.Id!)}", cancellationToken);
            if (detail is not null) details.Add(detail);
        }

        return new CompanionState(blocking.Config, schedules, details);
    }

    /// <param name="isUsable">
    /// Whether the answer is one the companion could actually give, for the calls it serves out of
    /// the DNS node rather than out of itself. **A rejected node token is not a 401.** The
    /// companion signs in to the node once, on our own login, and keeps that token in the session;
    /// when the node later rejects it - which one moment of the node being unreachable is enough to
    /// cause - the companion drops it and needs a fresh login to mint another. Until then it goes
    /// on answering 200, because the request to the companion itself succeeded, and simply leaves
    /// the node's half of the payload out. Read at face value that says the household filters
    /// nothing, which is why it is checked here and not left to the caller: it is the same fault as
    /// an expired session and the same thing fixes it.
    /// </param>
    private async Task<T?> GetAsync<T>(
        string path,
        CancellationToken cancellationToken,
        Func<T?, bool>? isUsable = null)
    {
        await EnsureSignedInAsync(cancellationToken);
        var (status, succeeded, value) = await SendAsync<T>(path, cancellationToken);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (succeeded && isUsable is not null && !isUsable(value)))
        {
            // The session lasts eight hours and the server outlives it; the node's token can be
            // gone long before that. One silent renewal is the ordinary case either way, not a
            // failure worth telling the parent about.
            _signedIn = false;
            await EnsureSignedInAsync(cancellationToken);
            (status, succeeded, value) = await SendAsync<T>(path, cancellationToken);
        }

        if (!succeeded)
        {
            _logger.LogWarning("The DNS companion answered {Status} for {Path}.", (int)status, path);
            return default;
        }

        return value;
    }

    private async Task<(HttpStatusCode Status, bool Succeeded, T? Value)> SendAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        return response.IsSuccessStatusCode
            ? (response.StatusCode, true, await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))
            : (response.StatusCode, false, default);
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

    /// <summary>
    /// Everything one read of the companion returns, before it means anything. Every part of it
    /// was actually read: <see cref="ReadAsync"/> throws rather than hand over a gap.
    /// </summary>
    public sealed record CompanionState(
        AdvancedBlockingConfig Blocking,
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
