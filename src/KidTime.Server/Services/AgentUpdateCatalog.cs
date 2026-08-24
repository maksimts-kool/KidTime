using System.Security.Cryptography;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.Server.Services;

public sealed class AgentUpdateCatalog(IConfiguration configuration, ILogger<AgentUpdateCatalog> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(configuration["AgentUpdates:Directory"] ?? "/updates");

    public AgentUpdatePackage? GetLatest()
    {
        var manifestPath = Path.Combine(_directory, "latest.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var source = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (source is null || !Version.TryParse(source.Version, out _) || string.IsNullOrWhiteSpace(source.FileName)
                || source.Sha256.Length != 64 || source.SizeBytes <= 0)
                return null;
            var packagePath = Path.GetFullPath(Path.Combine(_directory, source.FileName));
            var relative = Path.GetRelativePath(_directory, packagePath);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || !File.Exists(packagePath))
                return null;
            var file = new FileInfo(packagePath);
            if (file.Length != source.SizeBytes) return null;
            return new AgentUpdatePackage(
                new AgentUpdateManifest(source.Version, source.Sha256.ToLowerInvariant(), source.SizeBytes),
                packagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Agent update manifest could not be read.");
            return null;
        }
    }

    public bool IsCurrent(string? installedVersion)
    {
        var latest = GetLatest()?.Manifest.Version;
        return latest is not null && Version.TryParse(installedVersion, out var installed)
               && Version.TryParse(latest, out var available) && installed >= available;
    }

    public async Task<bool> VerifyPackageAsync(AgentUpdatePackage package, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(package.PackagePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(hash, package.Manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReleaseManifest(string Version, string FileName, string Sha256, long SizeBytes);
}

public sealed record AgentUpdatePackage(AgentUpdateManifest Manifest, string PackagePath);
