using System.Text.Json;
using Microsoft.Data.Sqlite;
using OcrWorkbench.Contracts;
using OcrWorkbench.Infrastructure;

namespace OcrWorkbench.Core.Tests;

public sealed class SqliteJobStoreTests
{
    [Fact]
    public async Task Migrates_legacy_nonterminal_jobs_to_failed_and_preserves_terminal_jobs()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-migration-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "jobs.db");
            var queuedId = Guid.NewGuid();
            var completedId = Guid.NewGuid();
            var spec = new JobSpec(
                [new ImageInput("input.png", "image/png")],
                new RecognitionOptions(),
                new PlainTextExportOptions("output.txt"));
            await CreateLegacyDatabaseAsync(databasePath, queuedId, completedId, spec);
            var store = new SqliteJobStore(databasePath);

            await store.InitializeAsync();

            var queued = await store.GetAsync(queuedId);
            var completed = await store.GetAsync(completedId);
            Assert.Equal(JobState.Failed, queued?.State);
            Assert.Equal("Host.LegacyJobNotResumable", queued?.ErrorCode);
            Assert.Equal(JobState.Completed, completed?.State);
            Assert.Null(completed?.Recognizer);
            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
                await using var pageTable = connection.CreateCommand();
                pageTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'job_pages';";
                Assert.Equal(1L, (long)(await pageTable.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PersistsJobAndUsesCompareAndSwapTransitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteJobStore(Path.Combine(directory, "jobs.db"));
            await store.InitializeAsync();
            var spec = new JobSpec(
                [new ImageInput("input.png", "image/png")],
                new RecognitionOptions("zh-Hans"),
                new PlainTextExportOptions("output.txt"));

            var queued = await store.EnqueueAsync(
                spec,
                new RecognizerIdentity("org.ocrworkbench.test", "1.0.0"));
            Assert.True(await store.TransitionAsync(queued.Id, JobState.Queued, JobState.Running));
            Assert.False(await store.TransitionAsync(queued.Id, JobState.Queued, JobState.Cancelled));

            var running = await store.GetAsync(queued.Id);
            Assert.NotNull(running);
            Assert.Equal(JobState.Running, running.State);
            Assert.Equal("zh-Hans", running.Spec.Recognition.Language);
            Assert.Equal("org.ocrworkbench.test", running.Recognizer?.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task CreateLegacyDatabaseAsync(
        string databasePath,
        Guid queuedId,
        Guid completedId,
        JobSpec spec)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE jobs (
                id TEXT PRIMARY KEY,
                spec_json TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL
            );
            INSERT INTO jobs(id, spec_json, state, created_utc, updated_utc)
            VALUES ($queuedId, $spec, $queued, $timestamp, $timestamp);
            INSERT INTO jobs(id, spec_json, state, created_utc, updated_utc)
            VALUES ($completedId, $spec, $completed, $timestamp, $timestamp);
            """;
        command.Parameters.AddWithValue("$queuedId", queuedId.ToString("D"));
        command.Parameters.AddWithValue("$completedId", completedId.ToString("D"));
        command.Parameters.AddWithValue("$spec", JsonSerializer.Serialize(
            spec,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("$queued", (int)JobState.Queued);
        command.Parameters.AddWithValue("$completed", (int)JobState.Completed);
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
