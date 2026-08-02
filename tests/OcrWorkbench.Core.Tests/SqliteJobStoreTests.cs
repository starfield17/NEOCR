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
                Assert.Equal(3L, (long)(await version.ExecuteScalarAsync())!);
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
            var now = DateTimeOffset.UtcNow;
            var runId = Guid.NewGuid();
            Assert.NotNull(await store.TryClaimAsync(
                queued.Id, runId, now, now.AddMinutes(1)));
            Assert.False(await store.TransitionAsync(queued.Id, JobState.Queued, JobState.Cancelled));

            var running = await store.GetAsync(queued.Id);
            Assert.NotNull(running);
            Assert.Equal(JobState.Running, running.State);
            Assert.Equal("zh-Hans", running.Spec.Recognition.Language);
            Assert.Equal("org.ocrworkbench.test", running.Recognizer?.Id);
            Assert.Equal(runId, running.RunId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciles_expired_run_and_fences_its_old_owner_without_losing_pages()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteJobStore(Path.Combine(directory, "jobs.db"));
            await store.InitializeAsync();
            var spec = new JobSpec(
                [new ImageInput("one.png", "image/png"), new ImageInput("two.png", "image/png")],
                new RecognitionOptions(),
                new PlainTextExportOptions("output.txt"));
            var job = await store.EnqueueAsync(
                spec,
                new RecognizerIdentity("org.ocrworkbench.test", "1.0.0"));
            var issuedAt = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);
            var oldRunId = Guid.NewGuid();
            Assert.NotNull(await store.TryClaimAsync(
                job.Id, oldRunId, issuedAt, issuedAt.AddMinutes(1)));
            var pages = new[]
            {
                new PageArtifact("one", "one.png", "image/png", 1),
                new PageArtifact("two", "two.png", "image/png", 2),
            };
            await store.InitializePagesAsync(job.Id, oldRunId, issuedAt, pages);
            Assert.True(await store.SavePageSucceededAsync(
                job.Id,
                oldRunId,
                issuedAt,
                0,
                "one",
                new RecognitionResult([])));

            var recovered = await store.ReconcileAbandonedAsync(issuedAt.AddMinutes(2));

            Assert.Equal(job.Id, Assert.Single(recovered));
            var paused = Assert.Single(await store.ListAsync(JobState.Paused));
            Assert.Null(paused.RunId);
            Assert.Null(paused.LeaseExpiresAt);
            var retainedPages = await store.GetPagesAsync(job.Id);
            Assert.Equal(2, retainedPages.Count);
            Assert.Equal(PageCheckpointState.Succeeded, retainedPages[0].State);
            Assert.False(await store.SavePageDeclinedAsync(
                job.Id,
                oldRunId,
                issuedAt.AddMinutes(2),
                1,
                "two",
                "old",
                "old owner"));
            Assert.False(await store.TransitionOwnedAsync(
                job.Id,
                oldRunId,
                issuedAt.AddMinutes(2),
                JobState.Running,
                JobState.Failed));

            Assert.True(await store.TransitionAsync(
                job.Id, JobState.Paused, JobState.Queued));
            var newRunId = Guid.NewGuid();
            var reclaimedAt = issuedAt.AddMinutes(3);
            Assert.NotNull(await store.TryClaimAsync(
                job.Id, newRunId, reclaimedAt, reclaimedAt.AddMinutes(1)));
            Assert.False(await store.RenewLeaseAsync(
                job.Id,
                oldRunId,
                reclaimedAt,
                reclaimedAt.AddMinutes(1)));
            Assert.False(await store.TransitionOwnedAsync(
                job.Id,
                oldRunId,
                reclaimedAt,
                JobState.Running,
                JobState.Failed));
            Assert.False(await store.SavePageDeclinedAsync(
                job.Id,
                oldRunId,
                reclaimedAt,
                1,
                "two",
                "old",
                "old owner"));
            Assert.True(await store.SavePageSucceededAsync(
                job.Id,
                newRunId,
                reclaimedAt,
                1,
                "two",
                new RecognitionResult([])));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Reconciliation_does_not_pause_a_live_run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-live-lease-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteJobStore(Path.Combine(directory, "jobs.db"));
            await store.InitializeAsync();
            var job = await store.EnqueueAsync(
                new JobSpec(
                    [new ImageInput("input.png", "image/png")],
                    new RecognitionOptions(),
                    new PlainTextExportOptions("output.txt")),
                new RecognizerIdentity("org.ocrworkbench.test", "1.0.0"));
            var now = DateTimeOffset.UtcNow;
            Assert.NotNull(await store.TryClaimAsync(
                job.Id, Guid.NewGuid(), now, now.AddMinutes(1)));

            Assert.Empty(await store.ReconcileAbandonedAsync(now.AddMinutes(1).AddTicks(-1)));
            Assert.Equal(JobState.Running, (await store.GetAsync(job.Id))?.State);
            Assert.Equal(job.Id, Assert.Single(
                await store.ReconcileAbandonedAsync(now.AddMinutes(1))));
            Assert.Equal(JobState.Paused, (await store.GetAsync(job.Id))?.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(JobState.Running)]
    [InlineData(JobState.Pausing)]
    public async Task Migrated_v2_run_without_a_lease_is_recovered_and_keeps_its_checkpoint(
        JobState initialState)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-v2-recovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "jobs.db");
            var jobId = Guid.NewGuid();
            var spec = new JobSpec(
                [new ImageInput("input.png", "image/png")],
                new RecognitionOptions(),
                new PlainTextExportOptions("output.txt"));
            await CreateV2DatabaseAsync(databasePath, jobId, initialState, spec);
            var store = new SqliteJobStore(databasePath);

            await store.InitializeAsync();
            Assert.Equal(initialState, (await store.GetAsync(jobId))?.State);

            var recovered = await store.ReconcileAbandonedAsync(DateTimeOffset.UtcNow);

            Assert.Equal(jobId, Assert.Single(recovered));
            Assert.Equal(JobState.Paused, (await store.GetAsync(jobId))?.State);
            Assert.Single(await store.GetPagesAsync(jobId));
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

    private static async Task CreateV2DatabaseAsync(
        string databasePath,
        Guid jobId,
        JobState state,
        JobSpec spec)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 2;
            CREATE TABLE jobs (
                id TEXT PRIMARY KEY,
                spec_json TEXT NOT NULL,
                state INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                recognizer_id TEXT NULL,
                recognizer_version TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL
            );
            CREATE TABLE job_pages (
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
            INSERT INTO jobs(
                id, spec_json, state, created_utc, updated_utc,
                recognizer_id, recognizer_version)
            VALUES ($id, $spec, $state, $timestamp, $timestamp, $recognizerId, $recognizerVersion);
            INSERT INTO job_pages(
                job_id, input_index, stable_id, source_path, mime_type,
                page_number, state, updated_utc)
            VALUES ($id, 0, 'stable', 'input.png', 'image/png', 1, $pageState, $timestamp);
            """;
        command.Parameters.AddWithValue("$id", jobId.ToString("D"));
        command.Parameters.AddWithValue("$spec", JsonSerializer.Serialize(
            spec,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
        command.Parameters.AddWithValue("$recognizerId", "org.ocrworkbench.test");
        command.Parameters.AddWithValue("$recognizerVersion", "1.0.0");
        command.Parameters.AddWithValue("$pageState", (int)PageCheckpointState.Succeeded);
        await command.ExecuteNonQueryAsync();
    }
}
