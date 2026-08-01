using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OcrWorkbench.Contracts;
using OcrWorkbench.Core;

namespace OcrWorkbench.Infrastructure;

public sealed class SqliteJobStore(string databasePath) : IJobStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(databasePath),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS jobs (
                id TEXT PRIMARY KEY,
                spec_json TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_jobs_state_created
                ON jobs(state, created_utc);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord> EnqueueAsync(JobSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var now = DateTimeOffset.UtcNow;
        var record = new JobRecord(Guid.NewGuid(), spec, JobState.Queued, now, now);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs(id, spec_json, state, created_utc, updated_utc)
            VALUES ($id, $spec, $state, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$spec", JsonSerializer.Serialize(spec, JsonOptions));
        command.Parameters.AddWithValue("$state", (int)record.State);
        command.Parameters.AddWithValue("$created", FormatTimestamp(record.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(record.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT spec_json, state, created_utc, updated_utc, error_code, error_message
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
        return new JobRecord(
            id,
            spec,
            (JobState)reader.GetInt32(1),
            ParseTimestamp(reader.GetString(2)),
            ParseTimestamp(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task<bool> TransitionAsync(
        Guid id,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureTransition(expected, target);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET state = $target,
                updated_utc = $updated,
                error_code = $errorCode,
                error_message = $errorMessage
            WHERE id = $id AND state = $expected;
            """;
        command.Parameters.AddWithValue("$target", (int)target);
        command.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$expected", (int)expected);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string timestamp) =>
        DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

