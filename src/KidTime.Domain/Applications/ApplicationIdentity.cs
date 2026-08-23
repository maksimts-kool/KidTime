using System.Security.Cryptography;
using System.Text;

namespace KidTime.Domain.Applications;

public sealed class ApplicationDescriptor
{
    public string DisplayName { get; init; } = "Unknown application";
    public string ExecutableName { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public string? OriginalFilename { get; init; }
    public string? Company { get; init; }
    public string? SignaturePublisher { get; init; }
    public string? FileVersion { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? Sha256 { get; init; }
    public string? IconPngBase64 { get; init; }
}

public static class ApplicationIdentity
{
    public static string CreateKey(ApplicationDescriptor application)
    {
        var package = Normalize(application.PackageFamilyName);
        if (package.Length > 0)
        {
            return Hash($"package|{package}");
        }

        var publisher = Normalize(application.SignaturePublisher ?? application.Company);
        var product = Normalize(application.ProductName);
        var original = Normalize(application.OriginalFilename);
        var executable = Normalize(application.ExecutableName);

        if (publisher.Length > 0 && product.Length > 0 && executable.Length > 0)
        {
            // Installer catalog entries often omit OriginalFilename while the running
            // executable includes it. Publisher + product + executable keeps both
            // observations on the same rule (notably Microsoft Edge) without merging helpers.
            return Hash($"signed|{publisher}|{product}|{executable}");
        }

        if (publisher.Length > 0 && product.Length > 0)
        {
            return Hash($"signed-product|{publisher}|{product}");
        }

        if (publisher.Length > 0 && original.Length > 0)
        {
            return Hash($"signed-file|{publisher}|{original}");
        }

        if (product.Length > 0 && original.Length > 0)
        {
            return Hash($"product|{product}|{original}");
        }

        return Hash($"path|{NormalizePath(application.ExecutablePath)}|{Normalize(application.ExecutableName)}");
    }

    public static int MatchScore(ApplicationDescriptor known, ApplicationDescriptor candidate)
    {
        if (Same(known.PackageFamilyName, candidate.PackageFamilyName, requireValue: true))
        {
            return 100;
        }

        var score = 0;
        if (Same(known.SignaturePublisher, candidate.SignaturePublisher, requireValue: true)) score += 40;
        if (Same(known.ProductName, candidate.ProductName, requireValue: true)) score += 25;
        if (Same(known.OriginalFilename, candidate.OriginalFilename, requireValue: true)) score += 25;
        if (Same(known.Company, candidate.Company, requireValue: true)) score += 10;
        if (Same(known.ExecutableName, candidate.ExecutableName, requireValue: true)) score += 10;
        if (Same(NormalizePath(known.ExecutablePath), NormalizePath(candidate.ExecutablePath), requireValue: true)) score += 15;
        return Math.Min(score, 100);
    }

    public static bool IsSameApplication(ApplicationDescriptor known, ApplicationDescriptor candidate) =>
        CreateKey(known) == CreateKey(candidate) || MatchScore(known, candidate) >= 65;

    private static bool Same(string? left, string? right, bool requireValue)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);
        return (!requireValue || normalizedLeft.Length > 0) && normalizedLeft == normalizedRight;
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizePath(string? value) =>
        (value ?? string.Empty).Trim().Replace('/', '\\').ToLowerInvariant();

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
}
