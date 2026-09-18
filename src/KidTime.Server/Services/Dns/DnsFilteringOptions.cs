namespace KidTime.Server.Services.Dns;

/// <summary>
/// How to reach the household's Technitium DNS Companion, and which of its filtering groups this
/// installation's children fall in. Everything here is optional: with no <see cref="ApiUrl"/> the
/// feature is simply absent, the panel says so, and the child's window never draws the tab.
/// </summary>
public sealed class DnsFilteringOptions
{
    public const string SectionName = "Dns";

    /// <summary>
    /// The companion's API, as the KidTime server reaches it. On one Docker host that is the
    /// container name; across a LAN it is the published address.
    /// </summary>
    public string? ApiUrl { get; set; }

    /// <summary>
    /// The companion's address as the parent's browser reaches it, which is what the panel's
    /// button opens. It differs from <see cref="ApiUrl"/> whenever the server talks to a
    /// container name the browser has never heard of. Defaults to <see cref="ApiUrl"/>.
    /// </summary>
    public string? ConsoleUrl { get; set; }

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>The companion's id for the DNS server itself. One node is the ordinary case.</summary>
    public string NodeId { get; set; } = "node1";

    /// <summary>
    /// Which advanced-blocking group this household's PCs fall in. The companion maps groups to
    /// networks, and the KidTime server sees the device through whatever proxying stands between
    /// them, so the group is named rather than inferred from an address that may not be the PC's.
    /// Left empty it means the only group there is, which is the usual shape of a home setup.
    /// </summary>
    public string? GroupName { get; set; }

    /// <summary>
    /// SHA-256 of the companion's certificate, checked only when ordinary chain and hostname
    /// validation fails - the same order the Windows agent uses against this server, and what
    /// makes the companion's self-signed certificate usable without trusting everything.
    /// </summary>
    public string? PinnedCertificateSha256 { get; set; }

    /// <summary>
    /// Accepts any certificate. It exists for a first look at a new deployment and is the wrong
    /// answer afterwards: these calls carry the DNS console's password.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>
    /// How long a read is reused before the DNS server is asked again. The configuration changes
    /// when a parent changes it, so this is about not hammering the companion on every sync of
    /// every PC rather than about freshness.
    /// </summary>
    public int RefreshSeconds { get; set; } = 120;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>What the parent's redirect button opens, or null when there is nothing to open.</summary>
    public string? ResolveConsoleUrl()
    {
        var url = string.IsNullOrWhiteSpace(ConsoleUrl) ? ApiUrl : ConsoleUrl;
        return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
    }
}
