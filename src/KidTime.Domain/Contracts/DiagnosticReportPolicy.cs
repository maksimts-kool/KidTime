using System.Security.Cryptography;
using System.Text;

namespace KidTime.Domain.Contracts;

/// <summary>
/// The single place that bounds and normalizes diagnostic reports. Reports travel from an
/// unelevated process over the named pipe and from an agent over the network, so both the
/// service and the server sanitize them with the same rules before storing or displaying them.
/// </summary>
public static class DiagnosticReportPolicy
{
    public const int MaximumReportsPerBatch = 25;
    public const int MaximumMessageLength = 300;
    public const int MaximumExceptionTypeLength = 200;
    public const int MaximumDetailLength = 4_000;
    public const int MaximumComponentLength = 32;
    public const int MaximumSeverityLength = 16;
    public const int MaximumVersionLength = 50;

    public static DiagnosticReport Normalize(DiagnosticReport report, string? componentOverride = null) => new(
        report.ReportId == Guid.Empty ? Guid.NewGuid() : report.ReportId,
        report.OccurredAtUtc == default ? DateTimeOffset.UtcNow : report.OccurredAtUtc,
        Clean(componentOverride ?? report.Component, MaximumComponentLength) ?? DiagnosticComponents.SessionAgent,
        NormalizeSeverity(report.Severity),
        Clean(report.Message, MaximumMessageLength) ?? "Unspecified failure",
        Clean(report.ExceptionType, MaximumExceptionTypeLength),
        Clean(report.Detail, MaximumDetailLength),
        Clean(report.AgentVersion, MaximumVersionLength));

    public static string NormalizeSeverity(string? severity) => severity?.Trim() switch
    {
        DiagnosticSeverities.Fatal => DiagnosticSeverities.Fatal,
        DiagnosticSeverities.Warning => DiagnosticSeverities.Warning,
        _ => DiagnosticSeverities.Error
    };

    /// <summary>
    /// Groups repeats of the same fault. Digits are folded out of the message so that counters,
    /// process ids, and timestamps do not turn one recurring bug into hundreds of rows.
    /// </summary>
    public static string CreateFingerprint(DiagnosticReport report)
    {
        var material = string.Join(
            '\n',
            report.Component,
            report.ExceptionType ?? string.Empty,
            FoldNumbers(report.Message),
            FirstLine(report.Detail));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32].ToLowerInvariant();
    }

    private static string FirstLine(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return string.Empty;
        var newline = detail.AsSpan().IndexOfAny('\r', '\n');
        return FoldNumbers(newline < 0 ? detail : detail[..newline]);
    }

    private static string FoldNumbers(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasDigit = false;
        foreach (var character in value)
        {
            var isDigit = char.IsAsciiDigit(character);
            if (isDigit)
            {
                if (!previousWasDigit) builder.Append('#');
            }
            else
            {
                builder.Append(character);
            }
            previousWasDigit = isDigit;
        }
        return builder.ToString();
    }

    private static string? Clean(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}
