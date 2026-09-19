using System.Diagnostics;
using KidTime.Server.Security;

namespace KidTime.Server.Infrastructure;

/// <summary>
/// One line per request, written when the request ends, and a correlation id that everything
/// written inside it carries.
///
/// ASP.NET Core's own request logging is off in this server (<c>Microsoft.AspNetCore</c> sits at
/// Warning) because it costs several lines per request and still does not say who was asking.
/// This says it in one: what was asked for, what came back, how long it took, and which PC or
/// which parent it was - which is the whole of what a household's log is ever used for, namely
/// working out whether a PC is actually reaching the server and what it was told when it did.
/// </summary>
internal static class RequestLogging
{
    /// <summary>
    /// The header the panel and any reverse proxy use to join their own log lines to this
    /// server's. An id that arrives is kept rather than replaced, so one click in the panel reads
    /// as one id across both containers; the answer always carries it back so a parent looking at
    /// a browser's network tab has the same string the log does.
    /// </summary>
    public const string RequestIdHeader = "X-Request-Id";

    /// <summary>
    /// Query parameters whose value is never written down. The server does not currently put a
    /// secret in a query string and should not start, but a log is exactly the wrong place to
    /// find out that something has - so the redaction is unconditional rather than a reaction.
    /// </summary>
    private static readonly string[] SensitiveQueryKeys =
        ["token", "code", "password", "secret", "key", "credential"];

    /// <summary>
    /// Paths whose ordinary success is not news. A health probe every few seconds and a hub that
    /// reconnects all day would otherwise be most of the log; a failure on either is still logged
    /// at its own level, because that is the part worth seeing.
    /// </summary>
    private static readonly string[] RoutinePathPrefixes = ["/health", "/hubs/"];

    public static IApplicationBuilder UseKidTimeRequestLogging(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Http");
        return app.Use(async (context, next) =>
        {
            var requestId = ResolveRequestId(context);
            context.TraceIdentifier = requestId;
            context.Response.Headers[RequestIdHeader] = requestId;

            var started = Stopwatch.GetTimestamp();
            // The scope has to still be open when the request's own line is written, or that one
            // line is the only one in the request without the id everything else carries.
            using (logger.BeginScope(new Dictionary<string, object?> { ["req"] = requestId }))
            {
                try
                {
                    await next();
                }
                catch (Exception exception)
                {
                    Write(logger, context, started, exception);
                    throw;
                }

                Write(logger, context, started, null);
            }
        });
    }

    private static void Write(ILogger logger, HttpContext context, long started, Exception? exception)
    {
        var status = exception is not null ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
        var level = ResolveLevel(context, status, exception);
        if (!logger.IsEnabled(level)) return;

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var actor = DescribeActor(context);
        // Two templates rather than one with an empty placeholder, so the structured half of the
        // same event carries the actor as a value that can be filtered on and omits it when there
        // was none, instead of a string that begins with a separator.
        if (actor is null)
        {
            logger.Log(level, exception, "{Method} {Path} {Status} in {ElapsedMs:F1} ms",
                context.Request.Method, DescribePath(context.Request), status, elapsed);
            return;
        }

        logger.Log(level, exception, "{Method} {Path} {Status} in {ElapsedMs:F1} ms · {Actor}",
            context.Request.Method, DescribePath(context.Request), status, elapsed, actor);
    }

    /// <summary>
    /// What went wrong matters more than that something did: a 401 from an agent is a revoked
    /// credential or a clock problem and a parent needs to see it, while a 404 on a path nobody
    /// typed is not worth a warning colour.
    /// </summary>
    private static LogLevel ResolveLevel(HttpContext context, int status, Exception? exception)
    {
        if (exception is not null || status >= StatusCodes.Status500InternalServerError) return LogLevel.Error;
        if (status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden) return LogLevel.Warning;
        if (status >= StatusCodes.Status400BadRequest) return LogLevel.Warning;
        return IsRoutine(context.Request.Path) ? LogLevel.Debug : LogLevel.Information;
    }

    private static bool IsRoutine(PathString path) =>
        path.Value is { } value
        && RoutinePathPrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path with its query, minus anything that looks like a secret. The value is replaced
    /// rather than the parameter dropped, so a log still shows that it was sent.
    /// </summary>
    private static string DescribePath(HttpRequest request)
    {
        if (!request.QueryString.HasValue) return request.Path.Value ?? "/";
        var parts = request.Query.Select(pair => SensitiveQueryKeys.Any(sensitive =>
                pair.Key.Contains(sensitive, StringComparison.OrdinalIgnoreCase))
            ? $"{pair.Key}=***"
            : $"{pair.Key}={pair.Value}");
        return $"{request.Path.Value}?{string.Join('&', parts)}";
    }

    /// <summary>
    /// Which PC or which parent was asking, read after the pipeline has run because that is when
    /// authentication has happened. A device is named by the short form of its id: the log is read
    /// beside a panel that shows the same id, and eight characters is enough to tell four PCs
    /// apart without the line being mostly a GUID.
    /// </summary>
    private static string? DescribeActor(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) return null;
        var name = user.Identity.Name;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return string.Equals(user.Identity.AuthenticationType, DeviceAuthenticationDefaults.Scheme, StringComparison.Ordinal)
            ? $"device {name[..Math.Min(8, name.Length)]}"
            : name;
    }

    /// <summary>
    /// An id that arrived from the panel or a proxy, or a fresh short one. An arriving value is
    /// bounded and stripped of anything that is not printable before it is put in a log line and
    /// echoed in a header - it is the one part of this that comes from outside.
    /// </summary>
    private static string ResolveRequestId(HttpContext context)
    {
        var supplied = context.Request.Headers[RequestIdHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            var cleaned = new string(supplied.Where(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(48).ToArray());
            if (cleaned.Length > 0) return cleaned;
        }

        return Guid.NewGuid().ToString("N")[..8];
    }
}
