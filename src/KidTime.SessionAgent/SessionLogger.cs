using System.Reflection;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.SessionAgent;

/// <summary>
/// Local log plus a crash spool. The tray agent runs in the child's session and cannot reach the
/// server itself, so faults are written to a file that survives a crash and are handed to the
/// control service over the named pipe on the next exchange. From there they reach the parent's
/// panel. Nothing here may throw: it is called from unhandled-exception handlers.
/// </summary>
internal static class SessionLogger
{
    private const long MaximumLogBytes = 2 * 1024 * 1024;
    private const long MaximumSpoolBytes = 128 * 1024;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KidTime", "logs");
    private static readonly string LogFile = Path.Combine(LogDirectory, "session-agent.ndjson");
    private static readonly string SpoolFile = Path.Combine(LogDirectory, "session-agent-faults.ndjson");

    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    public static void Information(string message, Exception? exception = null)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                if (new FileInfo(LogFile) is { Exists: true, Length: > MaximumLogBytes }) File.Delete(LogFile);
                var line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    level = exception is null ? "Information" : "Error",
                    message,
                    exception = exception?.ToString()
                });
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Losing a log line must never take the agent down with it.
            }
        }
    }

    /// <summary>Logs a fault and queues it for delivery to the parent's panel.</summary>
    public static void ReportFault(string severity, string message, Exception? exception = null)
    {
        Information(message, exception);
        Spool([
            new DiagnosticReport(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                DiagnosticComponents.SessionAgent,
                severity,
                message,
                exception?.GetType().FullName,
                exception?.ToString(),
                Version)
        ]);
    }

    /// <summary>Removes and returns the queued faults so they can be sent over the pipe.</summary>
    public static IReadOnlyList<DiagnosticReport> TakePendingReports()
    {
        lock (Gate)
        {
            if (!File.Exists(SpoolFile)) return [];
            string[] lines;
            try
            {
                lines = File.ReadAllLines(SpoolFile);
                File.Delete(SpoolFile);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            var reports = new List<DiagnosticReport>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<DiagnosticReport>(line, JsonOptions) is { } report)
                        reports.Add(report);
                }
                catch (JsonException) { }
            }

            if (reports.Count <= DiagnosticReportPolicy.MaximumReportsPerBatch) return reports;
            var batch = reports.Take(DiagnosticReportPolicy.MaximumReportsPerBatch).ToList();
            SpoolCore(reports.Skip(batch.Count).ToList());
            return batch;
        }
    }

    /// <summary>Returns faults to the queue when the pipe exchange that carried them failed.</summary>
    public static void Requeue(IReadOnlyList<DiagnosticReport> reports) => Spool(reports);

    private static void Spool(IReadOnlyList<DiagnosticReport> reports)
    {
        if (reports.Count == 0) return;
        lock (Gate) SpoolCore(reports);
    }

    /// <summary>Appends to the spool. The caller already holds <see cref="Gate"/>.</summary>
    private static void SpoolCore(IReadOnlyList<DiagnosticReport> reports)
    {
        if (reports.Count == 0) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            if (new FileInfo(SpoolFile) is { Exists: true, Length: > MaximumSpoolBytes }) return;
            File.AppendAllLines(
                SpoolFile,
                reports.Select(report => JsonSerializer.Serialize(
                    DiagnosticReportPolicy.Normalize(report, DiagnosticComponents.SessionAgent),
                    JsonOptions)));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
    }
}
