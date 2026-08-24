using System.Text;

namespace KidTime.Domain.Contracts;

/// <summary>
/// Packs the server URL and the one-time enrollment code into a single value so a parent
/// can transfer both required setup options to the controlled PC in one paste.
/// </summary>
public static class SetupCode
{
    private const string Prefix = "KTS1";

    public static string Create(string serverUrl, string enrollmentCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(enrollmentCode);
        var normalizedUrl = NormalizeUrl(serverUrl) ?? throw new ArgumentException("An HTTPS server URL is required.", nameof(serverUrl));
        return $"{Prefix}.{Encode(normalizedUrl)}.{enrollmentCode.Trim()}";
    }

    public static bool TryParse(string value, out string serverUrl, out string enrollmentCode)
    {
        serverUrl = string.Empty;
        enrollmentCode = string.Empty;
        var trimmed = value?.Trim() ?? string.Empty;
        if (!trimmed.StartsWith(Prefix + ".", StringComparison.OrdinalIgnoreCase)) return false;

        var parts = trimmed.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3) return false;
        if (!TryDecode(parts[1], out var decodedUrl)) return false;
        var normalizedUrl = NormalizeUrl(decodedUrl);
        if (normalizedUrl is null) return false;
        if (!EnrollmentCode.TryParse(parts[2], out _, out _)) return false;

        serverUrl = normalizedUrl;
        enrollmentCode = parts[2].Trim();
        return true;
    }

    private static string? NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return normalized.Length > 0 ? normalized : null;
    }

    private static string Encode(string value) => Convert
        .ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => "?" };
        if (padded.EndsWith('?')) return false;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
