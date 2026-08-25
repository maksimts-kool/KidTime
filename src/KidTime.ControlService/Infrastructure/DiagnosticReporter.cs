using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.ControlService.Infrastructure;

/// <summary>
/// Collects agent faults so a parent can read them in the web panel without touching the
/// controlled PC. Reporting has to work from anywhere - including an unhandled-exception
/// handler on a dying thread - so <see cref="Report"/> only appends one line to a spool file
/// and never awaits, logs, or throws. <see cref="FlushToStoreAsync"/> moves the spool into the
/// durable SQLite queue that <c>AgentWorker</c> uploads.
/// </summary>
public sealed class DiagnosticReporter
{
    private const int MaximumSpoolBytes = 512 * 1024;
    private static readonly TimeSpan RepeatSuppression = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastReported = new(StringComparer.Ordinal);
    private readonly object _spoolGate = new();
    private readonly string _spoolFile;

    public DiagnosticReporter() : this(AgentPaths.DiagnosticSpoolFile) { }

    public DiagnosticReporter(string spoolFile) => _spoolFile = spoolFile;

    public string AgentVersion { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public void ReportError(string message, Exception? exception = null) =>
        Report(DiagnosticSeverities.Error, message, exception);

    public void ReportFatal(string message, Exception? exception = null) =>
        Report(DiagnosticSeverities.Fatal, message, exception);

    public void Report(
        string severity,
        string message,
        Exception? exception = null,
        string component = DiagnosticComponents.ControlService)
    {
        Enqueue(new DiagnosticReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            component,
            severity,
            message,
            exception?.GetType().FullName,
            exception?.ToString(),
            AgentVersion));
    }

    /// <summary>Accepts a report produced by another component, such as the SessionAgent.</summary>
    public void Enqueue(DiagnosticReport report)
    {
        try
        {
            var normalized = DiagnosticReportPolicy.Normalize(report);
            if (!ShouldReport(DiagnosticReportPolicy.CreateFingerprint(normalized))) return;

            lock (_spoolGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_spoolFile)!);
                if (new FileInfo(_spoolFile) is { Exists: true, Length: > MaximumSpoolBytes }) return;
                File.AppendAllText(_spoolFile, JsonSerializer.Serialize(normalized, JsonOptions) + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Diagnostics must never destabilize enforcement. A lost report is acceptable.
        }
    }

    /// <summary>
    /// A crash-looping component would otherwise spool the same stack every two seconds. The
    /// server still counts every occurrence it receives, so suppressing repeats here costs the
    /// parent nothing but keeps the queue and the panel readable.
    /// </summary>
    private bool ShouldReport(string fingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastReported.Count > 500) _lastReported.Clear();
        var accepted = true;
        _lastReported.AddOrUpdate(fingerprint, now, (_, previous) =>
        {
            if (now - previous >= RepeatSuppression) return now;
            accepted = false;
            return previous;
        });
        return accepted;
    }

    /// <summary>Moves spooled reports into the durable upload queue. Safe to call repeatedly.</summary>
    public async Task FlushToStoreAsync(LocalStore store, CancellationToken cancellationToken)
    {
        string[] lines;
        lock (_spoolGate)
        {
            if (!File.Exists(_spoolFile)) return;
            try
            {
                lines = File.ReadAllLines(_spoolFile);
                File.Delete(_spoolFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            DiagnosticReport? report;
            try { report = JsonSerializer.Deserialize<DiagnosticReport>(line, JsonOptions); }
            catch (JsonException) { continue; }
            if (report is null) continue;
            var normalized = DiagnosticReportPolicy.Normalize(report);
            await store.QueueDiagnosticAsync(
                normalized,
                DiagnosticReportPolicy.CreateFingerprint(normalized),
                cancellationToken);
        }
    }
}
