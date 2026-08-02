using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;

namespace OcrWorkbench.Infrastructure;

public sealed class SqliteJobStore(string databasePath) : IJobStore
{
    private const int SchemaVersion = 3;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false,
        ForeignKeys = true,
    }.ToString();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var existingVersion = await GetSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (existingVersion > SchemaVersion)
        {
            throw new InvalidDataException(
                $"Job database schema {existingVersion} is newer than supported schema {SchemaVersion}.");
        }

        await using (var journalMode = connection.CreateCommand())
        {
            journalMode.CommandText = "PRAGMA journal_mode = WAL;";
            await journalMode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                spec_json TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                recognizer_id TEXT NULL,
                recognizer_version TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                run_id TEXT NULL,
                lease_expires_utc TEXT NULL
            );
            """, cancellationToken).ConfigureAwait(false);

        var columns = await GetJobColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!columns.Contains("recognizer_id"))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE jobs ADD COLUMN recognizer_id TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("recognizer_version"))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE jobs ADD COLUMN recognizer_version TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("run_id"))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE jobs ADD COLUMN run_id TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        }

        if (!columns.Contains("lease_expires_utc"))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE jobs ADD COLUMN lease_expires_utc TEXT NULL;",
                cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, """
            CREATE INDEX IF NOT EXISTS ix_jobs_state_created
                ON jobs(state, created_utc);

            CREATE INDEX IF NOT EXISTS ix_jobs_state_lease
                ON jobs(state, lease_expires_utc);

            CREATE TABLE IF NOT EXISTS job_pages (
                job_id TEXT NOT NULL,
                input_index INTEGER NOT NULL,
                stable_id TEXT NOT NULL,
                source_path TEXT NOT NULL,
                mime_type TEXT NOT NULL,
                page_number INTEGER NOT NULL,
                state INTEGER NOT NULL,
                result_json TEXT NULL,
                decline_reason_code TEXT NULL,
                decline_message TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(job_id, input_index),
                FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
            );
            """, cancellationToken).ConfigureAwait(false);

        await using (var legacy = connection.CreateCommand())
        {
            legacy.Transaction = transaction;
            legacy.CommandText = """
                UPDATE jobs
                SET state = $failed,
                    updated_utc = $updated,
                    error_code = 'Host.LegacyJobNotResumable',
                    error_message = 'This non-terminal job predates page checkpoints and cannot be resumed.'
                WHERE recognizer_id IS NULL
                  AND state IN ($queued, $running, $pausing, $paused);
                """;
            legacy.Parameters.AddWithValue("$failed", (int)JobState.Failed);
            legacy.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
            legacy.Parameters.AddWithValue("$queued", (int)JobState.Queued);
            legacy.Parameters.AddWithValue("$running", (int)JobState.Running);
            legacy.Parameters.AddWithValue("$pausing", (int)JobState.Pausing);
            legacy.Parameters.AddWithValue("$paused", (int)JobState.Paused);
            await legacy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(
            connection,
            transaction,
            $"PRAGMA user_version = {SchemaVersion};",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord> EnqueueAsync(
        JobSpec spec,
        RecognizerIdentity recognizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(recognizer);
        var now = DateTimeOffset.UtcNow;
        var record = new JobRecord(Guid.NewGuid(), spec, JobState.Queued, now, now, recognizer);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(
                id, spec_json, state, created_utc, updated_utc, recognizer_id, recognizer_version)
            VALUES ($id, $spec, $state, $created, $updated, $recognizerId, $recognizerVersion);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$spec", JsonSerializer.Serialize(spec, JsonOptions));
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$created", FormatTimestamp(record.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(record.UpdatedAt));
        command.Parameters.AddWithValue("$recognizerId", recognizer.Id);
        command.Parameters.AddWithValue("$recognizerVersion", recognizer.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT spec_json, state, created_utc, updated_utc,
                   recognizer_id, recognizer_version, error_code, error_message,
                   run_id, lease_expires_utc
            FROM jobs WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var spec = JsonSerializer.Deserialize<JobSpec>(reader.GetString(0), JsonOptions)
            ?? throw new InvalidDataException($"Stored job '{id}' has an invalid specification.");
        var recognizer = reader.IsDBNull(4) || reader.IsDBNull(5)
            ? null
            : new RecognizerIdentity(reader.GetString(4), reader.GetString(5));
        return ReadJob(id, spec, recognizer, reader);
    }

    public async Task<IReadOnlyList<JobRecord>> ListAsync(
        JobState state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, spec_json, state, created_utc, updated_utc,
                   recognizer_id, recognizer_version, error_code, error_message,
                   run_id, lease_expires_utc
            FROM jobs
            WHERE state = $state
            ORDER BY updated_utc DESC, id;
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<JobRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.Parse(reader.GetString(0));
            var spec = JsonSerializer.Deserialize<JobSpec>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException($"Stored job '{id}' has an invalid specification.");
            var recognizer = reader.IsDBNull(5) || reader.IsDBNull(6)
                ? null
                : new RecognizerIdentity(reader.GetString(5), reader.GetString(6));
            jobs.Add(new JobRecord(
                id,
                spec,
                (JobState)reader.GetInt32(2),
                ParseTimestamp(reader.GetString(3)),
                ParseTimestamp(reader.GetString(4)),
                recognizer,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10))));
        }

        return jobs;
    }

    public async Task<IReadOnlyList<Guid>> ReconcileAbandonedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET state = $paused,
                updated_utc = $now,
                run_id = NULL,
                lease_expires_utc = NULL
            WHERE state IN ($running, $pausing)
              AND (lease_expires_utc IS NULL OR lease_expires_utc <= $now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$paused", (int)JobState.Paused);
        command.Parameters.AddWithValue("$running", (int)JobState.Running);
        command.Parameters.AddWithValue("$pausing", (int)JobState.Pausing);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var recovered = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            recovered.Add(Guid.Parse(reader.GetString(0)));
        }

        return recovered;
    }

    public async Task<JobRunLease?> TryClaimAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        EnsureValidLease(now, leaseExpiresAt);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET state = $running,
                updated_utc = $now,
                error_code = NULL,
                error_message = NULL,
                run_id = $runId,
                lease_expires_utc = $leaseExpires
            WHERE id = $id AND state = $queued;
            """;
        command.Parameters.AddWithValue("$running", (int)JobState.Running);
        command.Parameters.AddWithValue("$queued", (int)JobState.Queued);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$leaseExpires", FormatTimestamp(leaseExpiresAt));
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
            ? new JobRunLease(jobId, runId, leaseExpiresAt)
            : null;
    }

    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        EnsureValidLease(now, leaseExpiresAt);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET lease_expires_utc = $leaseExpires,
                updated_utc = $now
            WHERE id = $id
              AND run_id = $runId
              AND state IN ($running, $pausing)
              AND lease_expires_utc > $now;
            """;
        AddLeaseParameters(command, jobId, runId, now);
        command.Parameters.AddWithValue("$leaseExpires", FormatTimestamp(leaseExpiresAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<PageCheckpoint>> GetPagesAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_index, stable_id, source_path, mime_type, page_number, state,
                   result_json, decline_reason_code, decline_message, updated_utc
            FROM job_pages
            WHERE job_id = $jobId
            ORDER BY input_index;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var pages = new List<PageCheckpoint>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var result = reader.IsDBNull(6)
                ? null
                : JsonSerializer.Deserialize<RecognitionResult>(reader.GetString(6), JsonOptions)
                  ?? throw new InvalidDataException(
                      $"Stored page {reader.GetInt32(0)} for job '{jobId}' has an invalid result.");
            pages.Add(new PageCheckpoint(
                jobId,
                reader.GetInt32(0),
                new PageArtifact(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4)),
                (PageCheckpointState)reader.GetInt32(5),
                result,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                ParseTimestamp(reader.GetString(9))));
        }

        return pages;
    }

    public async Task InitializePagesAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        IReadOnlyList<PageArtifact> pages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < pages.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO job_pages(
                    job_id, input_index, stable_id, source_path, mime_type,
                    page_number, state, updated_utc)
                SELECT $jobId, $inputIndex, $stableId, $sourcePath, $mimeType,
                       $pageNumber, $state, $updated
                WHERE EXISTS (
                    SELECT 1 FROM jobs
                    WHERE id = $jobId
                      AND run_id = $runId
                      AND state IN ($running, $pausing)
                      AND lease_expires_utc > $now);
                """;
            command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
            command.Parameters.AddWithValue("$inputIndex", index);
            command.Parameters.AddWithValue("$stableId", pages[index].StableId);
            command.Parameters.AddWithValue("$sourcePath", pages[index].SourcePath);
            command.Parameters.AddWithValue("$mimeType", pages[index].MimeType);
            command.Parameters.AddWithValue("$pageNumber", pages[index].PageNumber);
            command.Parameters.AddWithValue("$state", (int)PageCheckpointState.Pending);
            command.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            command.Parameters.AddWithValue("$runId", runId.ToString("D"));
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            command.Parameters.AddWithValue("$running", (int)JobState.Running);
            command.Parameters.AddWithValue("$pausing", (int)JobState.Pausing);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    $"Page {index} for job '{jobId}' could not be initialized.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SavePageSucceededAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        RecognitionResult result,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(
            jobId,
            runId,
            now,
            inputIndex,
            expectedStableId,
            PageCheckpointState.Succeeded,
            JsonSerializer.Serialize(result, JsonOptions),
            null,
            null,
            cancellationToken);

    public Task<bool> SavePageDeclinedAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        string reasonCode,
        string message,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(
            jobId,
            runId,
            now,
            inputIndex,
            expectedStableId,
            PageCheckpointState.Declined,
            null,
            reasonCode,
            message,
            cancellationToken);

    public async Task<bool> TransitionAsync(
        Guid id,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureTransition(expected, target);
        if (expected is JobState.Running or JobState.Pausing
            || target is JobState.Running or JobState.Pausing)
        {
            throw new InvalidOperationException(
                "Running job transitions require a run lease.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = $target,
                updated_utc = $updated,
                error_code = $errorCode,
                error_message = $errorMessage,
                run_id = NULL,
                lease_expires_utc = NULL
            WHERE id = $id AND state = $expected;
            """;
        command.Parameters.AddWithValue("$target", (int)target);
        command.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$expected", (int)expected);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (JobStateMachine.IsTerminal(target))
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM job_pages WHERE job_id = $jobId;";
            cleanup.Parameters.AddWithValue("$jobId", id.ToString("D"));
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TransitionOwnedAsync(
        Guid id,
        Guid runId,
        DateTimeOffset now,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureTransition(expected, target);
        if (expected is not (JobState.Running or JobState.Pausing))
        {
            throw new InvalidOperationException("An owned transition must start from a running state.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state = $target,
                updated_utc = $now,
                error_code = $errorCode,
                error_message = $errorMessage,
                run_id = CASE WHEN $clearLease = 1 THEN NULL ELSE run_id END,
                lease_expires_utc = CASE WHEN $clearLease = 1 THEN NULL ELSE lease_expires_utc END
            WHERE id = $id
              AND state = $expected
              AND run_id = $runId
              AND lease_expires_utc > $now;
            """;
        var clearLease = target == JobState.Paused || JobStateMachine.IsTerminal(target);
        command.Parameters.AddWithValue("$target", (int)target);
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$clearLease", clearLease ? 1 : 0);
        command.Parameters.AddWithValue("$expected", (int)expected);
        AddLeaseParameters(command, id, runId, now);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }

        if (JobStateMachine.IsTerminal(target))
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM job_pages WHERE job_id = $jobId;";
            cleanup.Parameters.AddWithValue("$jobId", id.ToString("D"));
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SavePageAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        PageCheckpointState state,
        string? resultJson,
        string? declineReasonCode,
        string? declineMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE job_pages
            SET state = $state,
                result_json = $result,
                decline_reason_code = $declineReason,
                decline_message = $declineMessage,
                updated_utc = $updated
            WHERE job_id = $jobId
              AND input_index = $inputIndex
              AND stable_id = $stableId
              AND state = $pending
              AND EXISTS (
                  SELECT 1 FROM jobs
                  WHERE id = $jobId
                    AND run_id = $runId
                    AND state IN ($running, $pausing)
                    AND lease_expires_utc > $now);
            """;
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$result", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$declineReason", (object?)declineReasonCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$declineMessage", (object?)declineMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", FormatTimestamp(now));
        command.Parameters.AddWithValue("$jobId", jobId.ToString("D"));
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$inputIndex", inputIndex);
        command.Parameters.AddWithValue("$stableId", expectedStableId);
        command.Parameters.AddWithValue("$pending", (int)PageCheckpointState.Pending);
        command.Parameters.AddWithValue("$running", (int)JobState.Running);
        command.Parameters.AddWithValue("$pausing", (int)JobState.Pausing);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<HashSet<string>> GetJobColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(jobs);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JobRecord ReadJob(
        Guid id,
        JobSpec spec,
        RecognizerIdentity? recognizer,
        SqliteDataReader reader) =>
        new(
            id,
            spec,
            (JobState)reader.GetInt32(1),
            ParseTimestamp(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            recognizer,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)));

    private static void AddLeaseParameters(
        SqliteCommand command,
        Guid jobId,
        Guid runId,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        command.Parameters.AddWithValue("$runId", runId.ToString("D"));
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$running", (int)JobState.Running);
        command.Parameters.AddWithValue("$pausing", (int)JobState.Pausing);
    }

    private static void EnsureValidLease(DateTimeOffset now, DateTimeOffset leaseExpiresAt)
    {
        if (leaseExpiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseExpiresAt),
                "A run lease must expire after its issue time.");
        }
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
