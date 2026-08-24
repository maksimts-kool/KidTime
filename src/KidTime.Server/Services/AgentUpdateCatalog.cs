using System.Security.Cryptography;
using System.Text.Json;
using KidTime.Domain.Contracts;

namespace KidTime.Server.Services;

public sealed class AgentUpdateCatalog(IConfiguration configuration, ILogger<AgentUpdateCatalog> logger)
{
    private const string InstallerFileName = "KidTimeSetup.exe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(configuration["AgentUpdates:Directory"] ?? "/updates");
    private volatile InstallerHash? _installerHash;

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

    public string? GetInstallerPath()
    {
        var installerPath = Path.GetFullPath(Path.Combine(_directory, InstallerFileName));
        var relative = Path.GetRelativePath(_directory, installerPath);
        return !relative.StartsWith("..", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative)
               && File.Exists(installerPath)
            ? installerPath
            : null;
    }

    /// <summary>
    /// Describes the downloadable Windows Setup executable so the web panel can show its
    /// version, size, and checksum before a parent copies it to the controlled PC.
    /// </summary>
    public InstallerPackage? GetInstaller()
    {
        var path = GetInstallerPath();
        if (path is null) return null;
        try
        {
            var file = new FileInfo(path);
            var cached = _installerHash;
            var hash = cached is not null && cached.Length == file.Length && cached.LastWriteTimeUtc == file.LastWriteTimeUtc
                ? cached.Sha256
                : null;
            if (hash is null)
            {
                using var stream = file.OpenRead();
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                _installerHash = new InstallerHash(file.Length, file.LastWriteTimeUtc, hash);
            }
            return new InstallerPackage(path, InstallerFileName, GetLatest()?.Manifest.Version, file.Length, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The Windows Setup executable could not be read.");
            return null;
        }
    }

    public async Task<bool> VerifyPackageAsync(AgentUpdatePackage package, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(package.PackagePath);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return string.Equals(hash, package.Manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReleaseManifest(string Version, string FileName, string Sha256, long SizeBytes);

    private sealed record InstallerHash(long Length, DateTime LastWriteTimeUtc, string Sha256);
}

public sealed record AgentUpdatePackage(AgentUpdateManifest Manifest, string PackagePath);

public sealed record InstallerPackage(string Path, string FileName, string? Version, long SizeBytes, string Sha256);
