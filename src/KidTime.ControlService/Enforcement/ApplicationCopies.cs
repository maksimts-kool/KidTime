using System.Diagnostics;
using System.Security.Cryptography;
using KidTime.ControlService.Infrastructure;
using KidTime.Domain.Rules;

namespace KidTime.ControlService.Enforcement;

/// <summary>
/// Recognizes an executable that is a byte-for-byte copy of an application carrying a rule.
///
/// An application's identity includes its executable name whenever the file carries no product
/// metadata, which is the case for cs2.exe. A child on a real controlled PC copied it to
/// <c>cs2_alt.exe</c> next to the original, and the copy was a new application with no rule on
/// it. The identity key cannot simply drop the name - that would change every existing key and
/// lose the rules a parent already set - so the copy is matched by content instead: the service
/// remembers the size and SHA-256 of every executable a rule applies to, and a process that has
/// no rule of its own but whose file is one of those is that application, for counting, warning
/// and closing alike. The fingerprints are kept on disk, so renaming the original away once it
/// has been seen is the same as copying it.
///
/// Only applications with a rule are fingerprinted, and a candidate file is hashed only when its
/// size already equals one of theirs. Every hash is kept for as long as the file it was taken
/// from is unchanged, so neither the two-second sample nor the process sweep reads a whole
/// executable twice.
/// </summary>
public sealed class ApplicationCopies(LocalStore store, ILogger logger)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CandidateRecheckInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, FileHash> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CandidateResult> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ApplicationFingerprint> _fingerprints = [];
    private bool _loaded;
    private long _refreshedRevision = -1;
    private long _refreshedTimestamp;

    /// <summary>
    /// Fingerprints the executable of every application with a rule. Cheap when nothing changed:
    /// it runs again only on a new rule revision or every ten minutes, and rehashes only a file
    /// whose size or modification time moved, which is what an application update looks like.
    /// </summary>
    public async Task RefreshAsync(DeviceRuleSnapshot rules, CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            var stored = await store.GetApplicationFingerprintsAsync(cancellationToken);
            lock (_lock) { _fingerprints = stored; _loaded = true; }
        }
        if (rules.Revision == _refreshedRevision && _refreshedTimestamp != 0
            && Stopwatch.GetElapsedTime(_refreshedTimestamp) < RefreshInterval)
            return;
        _refreshedRevision = rules.Revision;
        _refreshedTimestamp = Stopwatch.GetTimestamp();

        var ruled = rules.Applications.Select(rule => rule.IdentityKey).ToHashSet(StringComparer.Ordinal);
        var paths = await store.GetApplicationPathsAsync(ruled, cancellationToken);
        var added = false;
        foreach (var (identityKey, path) in paths)
        {
            if (TryHash(path) is not { } hash) continue;
            if (_fingerprints.Any(known => known.IdentityKey == identityKey && known.Sha256 == hash.Sha256)) continue;
            await store.AddApplicationFingerprintAsync(new ApplicationFingerprint(identityKey, hash.Sha256, hash.Length), cancellationToken);
            added = true;
        }
        if (!added) return;

        var reloaded = await store.GetApplicationFingerprintsAsync(cancellationToken);
        lock (_lock)
        {
            _fingerprints = reloaded;
            _candidates.Clear();
        }
    }

    /// <summary>
    /// The identity of the ruled application <paramref name="executablePath"/> is a copy of, or
    /// null when it is not a copy of one. A process that already has a rule of its own is never
    /// redirected: its own rule is the one the parent set on it.
    /// </summary>
    public string? FindOriginal(DeviceRuleSnapshot rules, string identityKey, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || rules.Applications.Any(rule => rule.IdentityKey == identityKey))
            return null;

        IReadOnlyList<ApplicationFingerprint> fingerprints;
        string? previous;
        lock (_lock)
        {
            fingerprints = _fingerprints;
            _candidates.TryGetValue(executablePath, out var cached);
            if (cached is not null && Stopwatch.GetElapsedTime(cached.CheckedTimestamp) < CandidateRecheckInterval)
                return Applicable(rules, cached.Original);
            previous = cached?.Original;
        }

        string? original = null;
        var length = TryGetLength(executablePath);
        if (length is long size && fingerprints.Any(known => known.Length == size)
            && TryHash(executablePath) is { } hash)
        {
            original = fingerprints
                .Where(known => known.Length == hash.Length && known.Sha256 == hash.Sha256)
                .Select(known => known.IdentityKey)
                .FirstOrDefault(key => key != identityKey && rules.Applications.Any(rule => rule.IdentityKey == key));
            if (original is not null && original != previous)
                logger.LogWarning("{Path} is a copy of a controlled application; that application's rule applies to it.",
                    executablePath);
        }

        lock (_lock) { _candidates[executablePath] = new CandidateResult(original, Stopwatch.GetTimestamp()); }
        return original;
    }

    private static string? Applicable(DeviceRuleSnapshot rules, string? original) =>
        original is not null && rules.Applications.Any(rule => rule.IdentityKey == original) ? original : null;

    private static long? TryGetLength(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            return null;
        }
    }

    private FileHash? TryHash(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            lock (_lock)
            {
                if (_hashes.TryGetValue(path, out var cached)
                    && cached.Length == file.Length && cached.LastWriteUtc == file.LastWriteTimeUtc)
                    return cached;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var hash = new FileHash(
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), file.Length, file.LastWriteTimeUtc);
            lock (_lock) { _hashes[path] = hash; }
            return hash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            logger.LogDebug(exception, "Could not fingerprint {Path}.", path);
            return null;
        }
    }

    private sealed record FileHash(string Sha256, long Length, DateTime LastWriteUtc);
    private sealed record CandidateResult(string? Original, long CheckedTimestamp);
}
