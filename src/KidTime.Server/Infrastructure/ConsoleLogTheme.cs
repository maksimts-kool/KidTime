namespace KidTime.Server.Infrastructure;

/// <summary>
/// The ANSI escapes <see cref="KidTimeConsoleFormatter"/> paints with, or empty strings when the
/// log is not going to a terminal.
///
/// Colour is decided once, at startup, from where the output actually goes. A container's stdout
/// is a pipe, so plain auto-detection would strip the colour out of exactly the place a parent
/// reads the log from - <c>docker compose logs</c> renders it perfectly well. The setting
/// therefore takes <c>auto</c>, <c>always</c>, and <c>never</c>, and the <c>NO_COLOR</c>
/// convention is honoured because a log file is not the place to argue with it.
/// </summary>
internal sealed class ConsoleLogTheme
{
    public static readonly ConsoleLogTheme Plain = new(false);
    public static readonly ConsoleLogTheme Coloured = new(true);

    private readonly bool _enabled;

    private ConsoleLogTheme(bool enabled) => _enabled = enabled;

    public static ConsoleLogTheme Resolve(string? preference)
    {
        var choice = (preference ?? "auto").Trim();
        if (choice.Equals("never", StringComparison.OrdinalIgnoreCase)) return Plain;
        if (choice.Equals("always", StringComparison.OrdinalIgnoreCase)) return Coloured;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return Plain;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
            return Plain;
        return Console.IsOutputRedirected ? Plain : Coloured;
    }

    public string Reset => _enabled ? "[0m" : string.Empty;
    public string Timestamp => Paint("38;5;245");
    public string Category => Paint("38;5;110");
    public string Detail => Paint("38;5;244");
    public string Message => Paint("38;5;252");

    public string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => Paint("38;5;240"),
        LogLevel.Debug => Paint("38;5;245"),
        LogLevel.Information => Paint("38;5;77"),
        LogLevel.Warning => Paint("38;5;214"),
        LogLevel.Error => Paint("38;5;203"),
        _ => Paint("1;38;5;199")
    };

    private string Paint(string code) => _enabled ? $"[{code}m" : string.Empty;
}
