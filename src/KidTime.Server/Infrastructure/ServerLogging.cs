using System.Reflection;
using KidTime.Server.Services;
using KidTime.Server.Services.Dns;
using Microsoft.Extensions.Logging.Console;

namespace KidTime.Server.Infrastructure;

/// <summary>
/// How the server writes its log, and what it says about itself the moment it is up.
///
/// There are two audiences and one switch between them rather than a compromise. A household runs
/// this on one box and reads it with <c>docker compose logs</c>, so the default is the column
/// format in <see cref="KidTimeConsoleFormatter"/>. Anything shipping the output to a collector
/// wants whole structured events instead, and gets them from the framework's own JSON console -
/// set <c>Logging__Format=json</c>.
/// </summary>
internal static class ServerLogging
{
    public static void AddKidTimeLogging(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Logging");
        var json = string.Equals(section["Format"], "json", StringComparison.OrdinalIgnoreCase);

        builder.Logging.ClearProviders();
        // The framework wraps every request in a scope carrying SpanId, TraceId and ParentId.
        // Nothing here is distributed - one container, one database - so those three are three
        // hex strings per line that no one will ever look up, and the request's own correlation
        // id does the job they were meant to do.
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.None);
        if (json)
        {
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
                options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
            });
            return;
        }

        builder.Logging.AddConsole(options => options.FormatterName = KidTimeConsoleFormatter.FormatterName);
        builder.Logging.AddConsoleFormatter<KidTimeConsoleFormatter, KidTimeConsoleFormatterOptions>(options =>
        {
            options.IncludeScopes = true;
            options.Colors = section["Colors"] ?? "auto";
            options.TimestampFormat = section["TimestampFormat"] ?? "HH:mm:ss";
            options.UseUtcTimestamp = bool.TryParse(section["UseUtcTimestamp"], out var utc) && utc;
            options.IncludeStackTrace = !bool.TryParse(section["IncludeStackTrace"], out var trace) || trace;
        });
    }

    /// <summary>
    /// What this installation actually is, written once, at the top of the log.
    ///
    /// Half of the questions a self-hosted deployment raises - why the agent cannot reach it, why
    /// the panel shows no filtering, why an update never arrives - are answered by knowing which
    /// version is running, which database it opened, whether the release directory has anything
    /// in it, and whether the DNS section was filled in. That is cheap to say once and expensive
    /// to work out afterwards over a chat message. No secret appears here: the connection string
    /// is reduced to its host and database, and nothing else on the line came from a password.
    /// </summary>
    public static void LogStartupSummary(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        // On ApplicationStarted rather than here, because that is when Kestrel has bound and the
        // addresses are something other than an empty list.
        lifetime.ApplicationStarted.Register(() =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
            logger.LogInformation(
                "KidTime server {Version} · {Environment} · listening on {Urls}",
                version,
                app.Environment.EnvironmentName,
                string.Join(", ", app.Urls.DefaultIfEmpty("(none)")));
            logger.LogInformation(
                "Database {Database} · agent updates {Updates} · web filtering {Filtering}",
                DescribeDatabase(app.Configuration.GetConnectionString("KidTime")),
                DescribeUpdates(app.Services.GetRequiredService<AgentUpdateCatalog>()),
                DescribeFiltering(app.Services.GetRequiredService<DnsFilteringOptions>()));
        });
        lifetime.ApplicationStopping.Register(() => logger.LogInformation("Shutting down."));
    }

    /// <summary>The host and database only. Everything else in a connection string is a credential.</summary>
    private static string DescribeDatabase(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "not configured";
        string? host = null, port = null, database = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("host", StringComparison.OrdinalIgnoreCase)) host = value;
            else if (key.Equals("port", StringComparison.OrdinalIgnoreCase)) port = value;
            else if (key.Equals("database", StringComparison.OrdinalIgnoreCase)) database = value;
        }

        return host is null ? "configured" : $"{host}:{port ?? "5432"}/{database ?? "?"}";
    }

    private static string DescribeUpdates(AgentUpdateCatalog catalog) =>
        catalog.GetLatest() is { } release ? $"{release.Manifest.Version} published" : "no release published";

    private static string DescribeFiltering(DnsFilteringOptions options) => options.IsConfigured
        ? $"reading {(string.IsNullOrWhiteSpace(options.GroupName) ? "the only group" : $"group '{options.GroupName}'")}"
        : "not configured";
}
