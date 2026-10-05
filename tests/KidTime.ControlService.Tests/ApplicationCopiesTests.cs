using KidTime.ControlService.Enforcement;
using KidTime.ControlService.Infrastructure;
using KidTime.ControlService.Server;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace KidTime.ControlService.Tests;

/// <summary>
/// A child on a real controlled PC copied cs2.exe to cs2_alt.exe beside it. cs2.exe carries no
/// product metadata, so its identity includes its file name, and the copy was a new application
/// with no rule on it.
/// </summary>
public sealed class ApplicationCopiesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "KidTime.Tests", Guid.NewGuid().ToString("N"));
    private string DatabaseFile => Path.Combine(_directory, "agent.db");
    private string GamePath => Path.Combine(_directory, "game", "cs2.exe");
    private string CopyPath => Path.Combine(_directory, "game", "cs2_alt.exe.exe");

    [Fact]
    public async Task A_renamed_copy_of_a_controlled_application_is_that_application()
    {
        var (store, rules) = await ArrangeAsync();
        File.Copy(GamePath, CopyPath);
        var copies = new ApplicationCopies(store, NullLogger.Instance);

        await copies.RefreshAsync(rules, CancellationToken.None);

        Assert.Equal(GameKey, copies.FindOriginal(rules, Key(CopyPath), CopyPath));
    }

    [Fact]
    public async Task Renaming_the_original_away_once_it_has_been_seen_is_the_same_as_copying_it()
    {
        var (store, rules) = await ArrangeAsync();
        await new ApplicationCopies(store, NullLogger.Instance).RefreshAsync(rules, CancellationToken.None);
        File.Move(GamePath, CopyPath);

        // A fresh instance is a service restart: what it knows comes from the database.
        var restarted = new ApplicationCopies(store, NullLogger.Instance);
        await restarted.RefreshAsync(rules, CancellationToken.None);

        Assert.Equal(GameKey, restarted.FindOriginal(rules, Key(CopyPath), CopyPath));
    }

    [Fact]
    public async Task A_different_executable_of_the_same_size_is_its_own_application()
    {
        var (store, rules) = await ArrangeAsync();
        var bytes = await File.ReadAllBytesAsync(GamePath);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(CopyPath, bytes);
        var copies = new ApplicationCopies(store, NullLogger.Instance);

        await copies.RefreshAsync(rules, CancellationToken.None);

        Assert.Null(copies.FindOriginal(rules, Key(CopyPath), CopyPath));
    }

    [Fact]
    public async Task An_application_with_a_rule_of_its_own_keeps_it()
    {
        var (store, rules) = await ArrangeAsync();
        File.Copy(GamePath, CopyPath);
        rules = new DeviceRuleSnapshot
        {
            Revision = 2,
            TimeZoneId = "UTC",
            Applications = [.. rules.Applications, new ApplicationRuleSnapshot { IdentityKey = Key(CopyPath), DisplayName = "cs2_alt" }]
        };
        var copies = new ApplicationCopies(store, NullLogger.Instance);

        await copies.RefreshAsync(rules, CancellationToken.None);

        Assert.Null(copies.FindOriginal(rules, Key(CopyPath), CopyPath));
    }

    [Fact]
    public async Task A_copy_stops_being_redirected_once_the_rule_it_escaped_is_removed()
    {
        var (store, rules) = await ArrangeAsync();
        File.Copy(GamePath, CopyPath);
        var copies = new ApplicationCopies(store, NullLogger.Instance);
        await copies.RefreshAsync(rules, CancellationToken.None);
        Assert.Equal(GameKey, copies.FindOriginal(rules, Key(CopyPath), CopyPath));

        var withoutRule = new DeviceRuleSnapshot { Revision = 2, TimeZoneId = "UTC" };

        Assert.Null(copies.FindOriginal(withoutRule, Key(CopyPath), CopyPath));
    }

    [Fact]
    public async Task Time_in_a_copy_is_counted_against_the_application_it_copies()
    {
        var (store, rules) = await ArrangeAsync();
        File.Copy(GamePath, CopyPath);
        var clock = new TrustedClock();
        var status = new AgentRuntimeStatus();
        status.MarkSynchronizationSucceeded();
        var coordinator = new EnforcementCoordinator(store, clock,
            new TimeExtensionService(store, NullLogger<TimeExtensionService>.Instance), status,
            NullLogger<EnforcementCoordinator>.Instance);
        coordinator.UpdateRules(rules);
        await coordinator.RefreshApplicationCopiesAsync(CancellationToken.None);
        var date = RuleEvaluator.GetLocalDate(clock.GetUtcNow(), "UTC");
        var copy = Descriptor(CopyPath);

        await coordinator.HandleSampleAsync(Sample(1, 0, copy), CancellationToken.None);
        await coordinator.HandleSampleAsync(Sample(2, 5_000, copy), CancellationToken.None);
        await coordinator.FlushUsageAsync(CancellationToken.None);

        Assert.Equal(5, await store.GetUsageAsync(date, GameKey, CancellationToken.None));
        Assert.Equal(0, await store.GetUsageAsync(date, Key(CopyPath), CancellationToken.None));
        Assert.Equal(GameKey, coordinator.ForegroundIdentity);
        Assert.Equal(GameKey, coordinator.ResolveApplicationCopy(Key(CopyPath), CopyPath));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string GameKey => Key(GamePath);

    private async Task<(LocalStore Store, DeviceRuleSnapshot Rules)> ArrangeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GamePath)!);
        var bytes = new byte[64 * 1024];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(GamePath, bytes);

        var store = new LocalStore(DatabaseFile);
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertApplicationAsync(GameKey, Descriptor(GamePath), CancellationToken.None);
        var rules = new DeviceRuleSnapshot
        {
            Revision = 1,
            TimeZoneId = "UTC",
            IdleThresholdSeconds = 300,
            Applications = [new ApplicationRuleSnapshot { IdentityKey = GameKey, DisplayName = "cs2", DailyLimitSeconds = 7200 }]
        };
        return (store, rules);
    }

    /// <summary>Shaped like cs2.exe: signed, with no product name or original filename.</summary>
    private static ApplicationDescriptor Descriptor(string path) => new()
    {
        DisplayName = Path.GetFileNameWithoutExtension(path),
        ExecutableName = Path.GetFileName(path),
        ExecutablePath = path,
        SignaturePublisher = "Valve Corp."
    };

    private static string Key(string path) =>
        ApplicationIdentity.CreateKey(ApplicationCatalogPolicy.NormalizeForCatalog(Descriptor(path)));

    private static SessionUsageSample Sample(long sequence, long elapsedMs, ApplicationDescriptor app) =>
        new(sequence, elapsedMs, false, 0, Environment.ProcessId, "Test", app);
}
