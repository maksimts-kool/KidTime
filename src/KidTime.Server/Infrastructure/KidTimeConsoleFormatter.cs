using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace KidTime.Server.Infrastructure;

/// <summary>
/// One line per entry, in columns, for a log a person reads.
///
/// The default ASP.NET Core console formatter spends two lines and a repeated fully-qualified
/// category on every entry, which on a self-hosted box is most of the screen and none of the
/// information. This keeps the time, a three-letter level, a short category and the message on
/// one line, puts the request's own identifiers in a dim trailer, and indents an exception under
/// the message it belongs to instead of flush left where it reads as unrelated output.
///
/// The machine-readable half of the same job is <c>AddJsonConsole</c>, which the server switches
/// to whole rather than trying to make one format serve both audiences - see
/// <see cref="ServerLogging"/>.
/// </summary>
internal sealed class KidTimeConsoleFormatter : ConsoleFormatter, IDisposable
{
    public const string FormatterName = "kidtime";

    /// <summary>
    /// How wide the category column is. Long enough for the names this server actually writes
    /// ("DnsFiltering", "DatabaseInitializer"), and anything longer is cut rather than allowed to
    /// push every message on the screen out of alignment.
    /// </summary>
    private const int CategoryWidth = 22;

    /// <summary>Wall clock to the second. A server log is read against a person's memory of when something happened.</summary>
    private const string DefaultTimestampFormat = "HH:mm:ss";

    /// <summary>
    /// Scope keys that are dropped rather than printed. Six of the seven are ASP.NET Core's own
    /// hosting and activity scopes: they are on every line inside a request, they repeat what the
    /// request's own log line already says, and together they are longer than any message this
    /// server writes - which is how a log ends up being scrolled past instead of read. The
    /// correlation id that replaces them is pushed by <c>RequestLogging</c>. The seventh is the
    /// placeholder every structured scope carries, which repeats the values printed beside it.
    /// </summary>
    private static readonly HashSet<string> FrameworkScopeKeys = new(StringComparer.Ordinal)
    {
        "{OriginalFormat}", "SpanId", "TraceId", "ParentId", "ConnectionId", "RequestId", "RequestPath",
        "ActionId", "ActionName"
    };

    private readonly IDisposable? _subscription;
    private KidTimeConsoleFormatterOptions _options;
    private ConsoleLogTheme _theme;

    public KidTimeConsoleFormatter(IOptionsMonitor<KidTimeConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
        _theme = ConsoleLogTheme.Resolve(_options.Colors);
        _subscription = options.OnChange(updated =>
        {
            _options = updated;
            _theme = ConsoleLogTheme.Resolve(updated.Colors);
        });
    }

    public override void Write<TState>(
        in LogEntry<TState> entry,
        IExternalScopeProvider? scopeProvider,
        TextWriter writer)
    {
        var message = entry.Formatter?.Invoke(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message) && entry.Exception is null) return;

        var theme = _theme;
        var builder = new StringBuilder(message?.Length + 96 ?? 128);
        var now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        builder.Append(theme.Timestamp)
            .Append(now.ToString(_options.TimestampFormat ?? DefaultTimestampFormat, CultureInfo.InvariantCulture))
            .Append(theme.Reset)
            .Append(' ')
            .Append(theme.Level(entry.LogLevel))
            .Append(Abbreviate(entry.LogLevel))
            .Append(theme.Reset)
            .Append(' ')
            .Append(theme.Category)
            .Append(Pad(ShortenCategory(entry.Category)))
            .Append(theme.Reset)
            .Append(' ')
            .Append(theme.Message)
            .Append(message)
            .Append(theme.Reset);

        AppendScopes(builder, theme, scopeProvider);
        AppendException(builder, theme, entry.Exception, message);
        writer.WriteLine(builder.ToString());
    }

    /// <summary>
    /// The scope values a request pushed - the correlation id, and anything a service added
    /// around its own work - as a dim trailer. They belong on every line inside the request, but
    /// never in front of the message: the message is what is being read, and the identifiers are
    /// what is being looked up afterwards.
    /// </summary>
    private void AppendScopes(StringBuilder builder, ConsoleLogTheme theme, IExternalScopeProvider? scopeProvider)
    {
        if (!_options.IncludeScopes || scopeProvider is null) return;
        var first = true;
        scopeProvider.ForEachScope((scope, state) =>
        {
            // Both shapes a scope arrives in: the list the message-template overload builds, and
            // the plain dictionary a caller passes when it only wants to name some values.
            if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            foreach (var pair in pairs)
            {
                if (pair.Value is null || FrameworkScopeKeys.Contains(pair.Key)) continue;
                state.Append(first ? $"{theme.Detail}  " : " ").Append(pair.Key).Append('=').Append(pair.Value);
                first = false;
            }
        }, builder);
        if (!first) builder.Append(theme.Reset);
    }

    /// <summary>
    /// The exception under its message, indented, with the type and its own message on the first
    /// line so the common case needs no scrolling. The stack trace follows dimmed, because it
    /// matters only once the first line has not been enough.
    /// </summary>
    private void AppendException(StringBuilder builder, ConsoleLogTheme theme, Exception? exception, string? message)
    {
        // Some libraries - Entity Framework most of all - format the whole exception into the
        // message they log it with. Printing the block underneath as well says everything twice
        // and buries the line that came before it.
        if (exception is not null && message?.Contains(exception.ToString(), StringComparison.Ordinal) == true) return;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine()
                .Append(theme.Level(LogLevel.Error))
                .Append("       ")
                .Append(current == exception ? "-> " : "   caused by ")
                .Append(current.GetType().Name)
                .Append(": ")
                .Append(current.Message)
                .Append(theme.Reset);
            if (!_options.IncludeStackTrace || current.StackTrace is null) continue;
            foreach (var line in current.StackTrace.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.Length == 0) continue;
                builder.AppendLine().Append(theme.Detail).Append("         ").Append(trimmed.Trim()).Append(theme.Reset);
            }
        }
    }

    /// <summary>
    /// The type name alone. A category is a fully-qualified name so that filters can be written
    /// against it, which is a different job from telling a reader where a line came from -
    /// "DevicesController" does that, and "KidTime.Server.Controllers.DevicesController" repeated
    /// down the screen does not.
    /// </summary>
    private static string ShortenCategory(string category)
    {
        var lastDot = category.LastIndexOf('.');
        var name = lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        // Every controller category ends the same way, so the suffix distinguishes nothing and
        // costs ten of the columns that would otherwise hold the part that does.
        return name.Length > "Controller".Length && name.EndsWith("Controller", StringComparison.Ordinal)
            ? name[..^"Controller".Length]
            : name;
    }

    private static string Pad(string category) => category.Length > CategoryWidth
        ? category[..(CategoryWidth - 1)] + "…"
        : category.PadRight(CategoryWidth);

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---"
    };

    public void Dispose() => _subscription?.Dispose();
}

internal sealed class KidTimeConsoleFormatterOptions : ConsoleFormatterOptions
{
    /// <summary><c>auto</c>, <c>always</c>, or <c>never</c>; see <see cref="ConsoleLogTheme"/>.</summary>
    public string Colors { get; set; } = "auto";

    /// <summary>
    /// Whether a logged exception brings its stack trace. On by default, because a fault a parent
    /// reports is usually one nobody can reproduce; off is for a busy server whose faults are
    /// already understood.
    /// </summary>
    public bool IncludeStackTrace { get; set; } = true;
}
