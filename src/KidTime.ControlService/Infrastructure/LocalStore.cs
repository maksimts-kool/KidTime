using System.Text.Json;
using KidTime.Domain.Applications;
using KidTime.Domain.Contracts;
using KidTime.Domain.Rules;
using Microsoft.Data.Sqlite;

namespace KidTime.ControlService.Infrastructure;

public sealed class LocalStore
{
    private const int MaximumQueuedDiagnostics = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _connectionString;

    public LocalStore() : this(AgentPaths.DatabaseFile) { }

    public LocalStore(string databaseFile)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AgentPaths.EnsureDirectories();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS rule_cache (
                    id INTEGER PRIMARY KEY CHECK(id = 1),
                    revision INTEGER NOT NULL,
                    payload_json TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS daily_usage (
                    local_date TEXT NOT NULL,
                    identity_key TEXT NOT NULL,
                    active_seconds INTEGER NOT NULL DEFAULT 0,
                    pending_seconds INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY(local_date, identity_key)
                );
                CREATE TABLE IF NOT EXISTS applications (
                    identity_key TEXT PRIMARY KEY,
                    descriptor_json TEXT NOT NULL,
                    first_seen_utc TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL,
                    synchronized_at_utc TEXT NULL
                );
                CREATE TABLE IF NOT EXISTS pending_batches (
                    batch_id TEXT PRIMARY KEY,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS diagnostics (
                    report_id TEXT PRIMARY KEY,
                    fingerprint TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS application_block_grace (
                    identity_key TEXT NOT NULL,
                    episode_key TEXT NOT NULL,
                    consumed_at_utc TEXT NOT NULL,
                    PRIMARY KEY(identity_key, episode_key)
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceRuleSnapshot?> LoadRulesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM rule_cache WHERE id=1";
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            return json is null ? null : JsonSerializer.Deserialize<DeviceRuleSnapshot>(json, JsonOptions);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveRulesAsync(DeviceRuleSnapshot rules, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(rules, JsonOptions);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO rule_cache(id,revision,payload_json,received_at_utc)
                VALUES(1,$revision,$json,$now)
                ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload_json=excluded.payload_json,received_at_utc=excluded.received_at_utc
                """;
            command.Parameters.AddWithValue("$revision", rules.Revision);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task UpsertApplicationAsync(
        string identityKey,
        ApplicationDescriptor descriptor,
        CancellationToken cancellationToken,
        bool forceSynchronization = false)
    {
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            if (descriptor.IconPngBase64 is null)
            {
                await using var select = connection.CreateCommand();
                select.CommandText = "SELECT descriptor_json FROM applications WHERE identity_key=$key";
                select.Parameters.AddWithValue("$key", identityKey);
                if (await select.ExecuteScalarAsync(cancellationToken) is string existingJson
                    && JsonSerializer.Deserialize<ApplicationDescriptor>(existingJson, JsonOptions) is { IconPngBase64: { Length: > 0 } } existing)
                {
                    descriptor = CopyWithIcon(descriptor, existing.IconPngBase64);
                }
            }
            var json = JsonSerializer.Serialize(descriptor, JsonOptions);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO applications(identity_key,descriptor_json,first_seen_utc,last_seen_utc,synchronized_at_utc)
                VALUES($key,$json,$now,$now,NULL)
                ON CONFLICT(identity_key) DO UPDATE SET descriptor_json=excluded.descriptor_json,last_seen_utc=excluded.last_seen_utc,
                    synchronized_at_utc=CASE
                        WHEN $forceSync=1 THEN NULL
                        WHEN applications.descriptor_json=excluded.descriptor_json THEN applications.synchronized_at_utc
                        ELSE NULL
                    END
                """;
            command.Parameters.AddWithValue("$key", identityKey);
            command.Parameters.AddWithValue("$json", json);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$forceSync", forceSynchronization ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private static ApplicationDescriptor CopyWithIcon(ApplicationDescriptor descriptor, string iconPngBase64) => new()
    {
        DisplayName = descriptor.DisplayName,
        ExecutableName = descriptor.ExecutableName,
        ExecutablePath = descriptor.ExecutablePath,
        ProductName = descriptor.ProductName,
        OriginalFilename = descriptor.OriginalFilename,
        Company = descriptor.Company,
        SignaturePublisher = descriptor.SignaturePublisher,
        FileVersion = descriptor.FileVersion,
        PackageFamilyName = descriptor.PackageFamilyName,
        Sha256 = descriptor.Sha256,
        IconPngBase64 = iconPngBase64
    };

    public async Task<IReadOnlyList<DiscoveredApplicationRequest>> GetApplicationsToSyncAsync(CancellationToken cancellationToken)
    {
        var result = new List<DiscoveredApplicationRequest>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT identity_key,descriptor_json,first_seen_utc,last_seen_utc FROM applications WHERE synchronized_at_utc IS NULL LIMIT 200";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new DiscoveredApplicationRequest(
                    reader.GetString(0),
                    JsonSerializer.Deserialize<ApplicationDescriptor>(reader.GetString(1), JsonOptions)!,
                    DateTimeOffset.Parse(reader.GetString(2)),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }
        }
        finally { _gate.Release(); }
        return result;
    }

    public async Task MarkApplicationSynchronizedAsync(string identityKey, CancellationToken cancellationToken)
    {
        await ExecuteAsync("UPDATE applications SET synchronized_at_utc=$now WHERE identity_key=$key", cancellationToken,
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$key", identityKey));
    }

    public async Task AddUsageAsync(DateOnly localDate, string? identityKey, int activeSeconds, CancellationToken cancellationToken)
    {
        if (activeSeconds <= 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO daily_usage(local_date,identity_key,active_seconds,pending_seconds)
                VALUES($date,$key,$seconds,$seconds)
                ON CONFLICT(local_date,identity_key) DO UPDATE SET
                    active_seconds=active_seconds+excluded.active_seconds,
                    pending_seconds=pending_seconds+excluded.pending_seconds
                """;
            command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$key", identityKey ?? string.Empty);
            command.Parameters.AddWithValue("$seconds", activeSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> GetUsageAsync(DateOnly localDate, string? identityKey, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT active_seconds FROM daily_usage WHERE local_date=$date AND identity_key=$key";
            command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$key", identityKey ?? string.Empty);
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        }
        finally { _gate.Release(); }
    }

    public async Task PrepareUsageBatchAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var deltas = new List<UsageDelta>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = "SELECT local_date,identity_key,pending_seconds FROM daily_usage WHERE pending_seconds > 0";
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    deltas.Add(new UsageDelta(
                        DateOnly.Parse(reader.GetString(0)),
                        string.IsNullOrEmpty(reader.GetString(1)) ? null : reader.GetString(1),
                        reader.GetInt32(2)));
                }
            }
            if (deltas.Count == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            var batch = new UsageBatchRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, deltas);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = "INSERT INTO pending_batches(batch_id,payload_json,created_at_utc) VALUES($id,$json,$now)";
                insert.Parameters.AddWithValue("$id", batch.BatchId.ToString());
                insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(batch, JsonOptions));
                insert.Parameters.AddWithValue("$now", batch.CreatedAtUtc.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var reset = connection.CreateCommand())
            {
                reset.Transaction = (SqliteTransaction)transaction;
                reset.CommandText = "UPDATE daily_usage SET pending_seconds=0 WHERE pending_seconds > 0";
                await reset.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<UsageBatchRequest>> GetPendingBatchesAsync(CancellationToken cancellationToken)
    {
        var batches = new List<UsageBatchRequest>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM pending_batches ORDER BY created_at_utc LIMIT 100";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                batches.Add(JsonSerializer.Deserialize<UsageBatchRequest>(reader.GetString(0), JsonOptions)!);
            }
        }
        finally { _gate.Release(); }
        return batches;
    }

    public Task CompleteBatchAsync(Guid batchId, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM pending_batches WHERE batch_id=$id", cancellationToken, ("$id", batchId.ToString()));

    public async Task<bool> TryConsumeFirstApplicationBlockGraceAsync(
        string identityKey,
        string episodeKey,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO application_block_grace(identity_key,episode_key,consumed_at_utc)
                VALUES($identity,$episode,$now)
                ON CONFLICT(identity_key,episode_key) DO NOTHING
                """;
            command.Parameters.AddWithValue("$identity", identityKey);
            command.Parameters.AddWithValue("$episode", episodeKey);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        finally { _gate.Release(); }
    }

    public Task<bool> TryConsumeFirstPcBlockGraceAsync(string episodeKey, CancellationToken cancellationToken) =>
        TryConsumeFirstApplicationBlockGraceAsync("__pc__", episodeKey, cancellationToken);

    /// <summary>
    /// Queues one fault for upload. The queue is bounded so a component that fails in a loop
    /// while the server is unreachable can never fill the controlled PC's disk.
    /// </summary>
    public async Task QueueDiagnosticAsync(
        DiagnosticReport report,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO diagnostics(report_id,fingerprint,payload_json,occurred_at_utc)
                VALUES($id,$fingerprint,$json,$occurred)
                ON CONFLICT(report_id) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$id", report.ReportId.ToString());
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(report, JsonOptions));
            insert.Parameters.AddWithValue("$occurred", report.OccurredAtUtc.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await using var trim = connection.CreateCommand();
            trim.CommandText = """
                DELETE FROM diagnostics WHERE report_id IN (
                    SELECT report_id FROM diagnostics ORDER BY occurred_at_utc DESC LIMIT -1 OFFSET $keep)
                """;
            trim.Parameters.AddWithValue("$keep", MaximumQueuedDiagnostics);
            await trim.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DiagnosticReport>> GetPendingDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var reports = new List<DiagnosticReport>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM diagnostics ORDER BY occurred_at_utc LIMIT $limit";
            command.Parameters.AddWithValue("$limit", DiagnosticReportPolicy.MaximumReportsPerBatch);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (JsonSerializer.Deserialize<DiagnosticReport>(reader.GetString(0), JsonOptions) is { } report)
                    reports.Add(report);
            }
        }
        finally { _gate.Release(); }
        return reports;
    }

    public async Task CompleteDiagnosticsAsync(IEnumerable<Guid> reportIds, CancellationToken cancellationToken)
    {
        foreach (var reportId in reportIds)
            await ExecuteAsync("DELETE FROM diagnostics WHERE report_id=$id", cancellationToken, ("$id", reportId.ToString()));
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
