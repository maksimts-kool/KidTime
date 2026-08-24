namespace KidTime.Domain.Contracts;

public static class EnrollmentCode
{
    private const string Prefix = "KT1";

    public static string Create(string token, string? certificatePin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var normalizedPin = NormalizePin(certificatePin);
        return normalizedPin is null ? token : $"{Prefix}.{token}.{normalizedPin}";
    }

    public static bool TryParse(string value, out string token, out string? certificatePin)
    {
        token = value.Trim();
        certificatePin = null;
        if (!token.StartsWith(Prefix + ".", StringComparison.OrdinalIgnoreCase))
            return token.Length > 0;

        var parts = token.Split('.', 3, StringSplitOptions.None);
        if (parts.Length != 3 || parts[1].Length < 20)
            return false;

        var normalizedPin = NormalizePin(parts[2]);
        if (normalizedPin is null || normalizedPin.Length != 64 || normalizedPin.Any(character => !Uri.IsHexDigit(character)))
            return false;

        token = parts[1];
        certificatePin = normalizedPin;
        return true;
    }

    private static string? NormalizePin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    }
}
