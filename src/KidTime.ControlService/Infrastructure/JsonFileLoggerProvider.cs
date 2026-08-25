using System.Text.Json;

namespace KidTime.ControlService.Infrastructure;

/// <summary>
/// Writes the service log and, for <see cref="LogLevel.Error"/> and above, hands the same entry
/// to <see cref="DiagnosticReporter"/> so the parent sees the failure in the web panel. Warnings
/// stay local: offline synchronization and transient IPC retries are expected operation.
/// </summary>
public sealed class JsonFileLoggerProvider(string path, DiagnosticReporter? diagnostics = null) : ILoggerProvider
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(categoryName, this);

    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (_lock)
        {
            _writer ??= CreateWriter();
            var payload = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                level = level.ToString(),
                category,
                eventId = eventId.Id,
                message,
                exception = exception?.ToString()
            });
            _writer.WriteLine(payload);
            _writer.Flush();
        }

        if (level >= LogLevel.Error) diagnostics?.ReportError($"{category}: {message}", exception);
    }

    private StreamWriter CreateWriter()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }

    private sealed class JsonFileLogger(string category, JsonFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
