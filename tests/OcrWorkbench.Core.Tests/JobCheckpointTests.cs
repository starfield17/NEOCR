using OcrWorkbench.Contracts;
using OcrWorkbench.Infrastructure;

namespace OcrWorkbench.Core.Tests;

public sealed class JobCheckpointTests
{
    [Fact]
    public async Task Cancellation_before_run_claims_job_and_cleans_it_as_cancelled()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(1);
        var recognizer = new ScriptedRecognizer();
        var service = new JobExecutionService(fixture.Store, recognizer);
        var job = await service.SubmitAsync(fixture.Spec);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RunAsync(job.Id, cancellation.Token));

        Assert.Equal(JobState.Cancelled, (await fixture.Store.GetAsync(job.Id))?.State);
        Assert.Empty(await fixture.Store.GetPagesAsync(job.Id));
        Assert.Empty(recognizer.RecognizedPaths);
    }

    [Fact]
    public async Task Pause_before_page_initialization_stops_at_the_initial_boundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neocr-early-pause-test-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.png");
            await File.WriteAllBytesAsync(input, [1]);
            var store = new MemoryJobStore();
            var enteredGetPages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGetPages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.BeforeGetPagesAsync = async _ =>
            {
                enteredGetPages.TrySetResult();
                await releaseGetPages.Task;
                store.BeforeGetPagesAsync = null;
            };
            var recognizer = new ScriptedRecognizer();
            var service = new JobExecutionService(store, recognizer);
            var job = await service.SubmitAsync(new JobSpec(
                [new ImageInput(input, "image/png")],
                new RecognitionOptions(),
                new PlainTextExportOptions(Path.Combine(directory, "output.txt"))));
            var execution = service.RunAsync(job.Id);
            await enteredGetPages.Task;

            var pauseRequested = await service.RequestPauseAsync(job.Id);
            releaseGetPages.TrySetResult();
            Assert.True(pauseRequested);
            var paused = Assert.IsType<JobExecutionResult.Paused>(await execution);

            Assert.Equal(0, paused.CompletedPages);
            Assert.Empty(recognizer.RecognizedPaths);
            Assert.Single(await store.GetPagesAsync(job.Id));
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Paused_job_resumes_with_new_service_without_reprocessing_completed_page()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(3);
        var firstRecognizer = new ScriptedRecognizer();
        var firstService = new JobExecutionService(fixture.Store, firstRecognizer);
        var job = await firstService.SubmitAsync(fixture.Spec);
        firstRecognizer.AfterRecognitionAsync = call => call == 1
            ? firstService.RequestPauseAsync(job.Id)
            : Task.FromResult(false);

        var paused = Assert.IsType<JobExecutionResult.Paused>(await firstService.RunAsync(job.Id));

        Assert.Equal(1, paused.CompletedPages);
        Assert.Equal(3, paused.TotalPages);
        Assert.Single(firstRecognizer.RecognizedPaths);
        Assert.Equal(PageCheckpointState.Succeeded, (await fixture.Store.GetPagesAsync(job.Id))[0].State);

        var reopenedStore = new SqliteJobStore(fixture.DatabasePath);
        await reopenedStore.InitializeAsync();
        var resumedRecognizer = new ScriptedRecognizer();
        var resumedService = new JobExecutionService(reopenedStore, resumedRecognizer);
        var finished = Assert.IsType<JobExecutionResult.Finished>(await resumedService.ResumeAsync(job.Id));

        Assert.Equal(JobState.Completed, finished.State);
        Assert.Equal(2, resumedRecognizer.RecognizedPaths.Count);
        Assert.DoesNotContain(fixture.InputPaths[0], resumedRecognizer.RecognizedPaths);
        var exported = await File.ReadAllTextAsync(fixture.OutputPath);
        Assert.Contains(Path.GetFileName(fixture.InputPaths[0]), exported);
        Assert.Contains(Path.GetFileName(fixture.InputPaths[1]), exported);
        Assert.Contains(Path.GetFileName(fixture.InputPaths[2]), exported);
        Assert.Empty(await reopenedStore.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task Pause_requested_during_last_page_finishes_job()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(1);
        var recognizer = new ScriptedRecognizer();
        var service = new JobExecutionService(fixture.Store, recognizer);
        var job = await service.SubmitAsync(fixture.Spec);
        recognizer.AfterRecognitionAsync = _ => service.RequestPauseAsync(job.Id);

        var finished = Assert.IsType<JobExecutionResult.Finished>(await service.RunAsync(job.Id));

        Assert.Equal(JobState.Completed, finished.State);
        Assert.True(File.Exists(fixture.OutputPath));
        Assert.Empty(await fixture.Store.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task Changed_input_fails_resume_and_cleans_temporary_results()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(2);
        var recognizer = new ScriptedRecognizer();
        var service = new JobExecutionService(fixture.Store, recognizer);
        var job = await service.SubmitAsync(fixture.Spec);
        recognizer.AfterRecognitionAsync = call => call == 1
            ? service.RequestPauseAsync(job.Id)
            : Task.FromResult(false);
        Assert.IsType<JobExecutionResult.Paused>(await service.RunAsync(job.Id));
        await File.WriteAllBytesAsync(fixture.InputPaths[1], [9, 9, 9, 9]);

        await Assert.ThrowsAsync<JobInputChangedException>(() => service.ResumeAsync(job.Id));

        var failed = await fixture.Store.GetAsync(job.Id);
        Assert.Equal(JobState.Failed, failed?.State);
        Assert.Equal("Input.Changed", failed?.ErrorCode);
        Assert.Empty(await fixture.Store.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task Recognizer_mismatch_keeps_job_paused_and_preserves_checkpoints()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(2);
        var recognizer = new ScriptedRecognizer();
        var service = new JobExecutionService(fixture.Store, recognizer);
        var job = await service.SubmitAsync(fixture.Spec);
        recognizer.AfterRecognitionAsync = call => call == 1
            ? service.RequestPauseAsync(job.Id)
            : Task.FromResult(false);
        Assert.IsType<JobExecutionResult.Paused>(await service.RunAsync(job.Id));
        var otherService = new JobExecutionService(
            fixture.Store,
            new ScriptedRecognizer(new RecognizerIdentity("org.ocrworkbench.other", "2.0.0")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => otherService.ResumeAsync(job.Id));

        Assert.Equal(JobState.Paused, (await fixture.Store.GetAsync(job.Id))?.State);
        Assert.NotEmpty(await fixture.Store.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task Declined_checkpoint_is_not_reprocessed_and_terminal_state_cleans_pages()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(2);
        var firstRecognizer = new ScriptedRecognizer
        {
            OutcomeFactory = (_, _) => new PluginOutcome<RecognitionResult>.Declined(
                DeclineReasonCodes.CapabilityMissing,
                "declined",
                false,
                []),
        };
        var firstService = new JobExecutionService(fixture.Store, firstRecognizer);
        var job = await firstService.SubmitAsync(fixture.Spec);
        firstRecognizer.AfterRecognitionAsync = call => call == 1
            ? firstService.RequestPauseAsync(job.Id)
            : Task.FromResult(false);
        Assert.IsType<JobExecutionResult.Paused>(await firstService.RunAsync(job.Id));
        var resumedRecognizer = new ScriptedRecognizer();
        var resumedService = new JobExecutionService(fixture.Store, resumedRecognizer);

        var finished = Assert.IsType<JobExecutionResult.Finished>(await resumedService.ResumeAsync(job.Id));

        Assert.Equal(JobState.CompletedWithErrors, finished.State);
        Assert.Single(finished.Pipeline.Declined);
        Assert.Single(resumedRecognizer.RecognizedPaths);
        Assert.Equal(fixture.InputPaths[1], resumedRecognizer.RecognizedPaths[0]);
        Assert.Empty(await fixture.Store.GetPagesAsync(job.Id));
    }

    [Fact]
    public async Task Explicit_failure_cleans_completed_page_results()
    {
        await using var fixture = await CheckpointFixture.CreateAsync(2);
        var recognizer = new ScriptedRecognizer
        {
            OutcomeFactory = (call, page) => call == 2
                ? new PluginOutcome<RecognitionResult>.Failed("Test.Failed", "failure", false)
                : Success(page),
        };
        var service = new JobExecutionService(fixture.Store, recognizer);
        var job = await service.SubmitAsync(fixture.Spec);

        await Assert.ThrowsAsync<RecognitionException>(() => service.RunAsync(job.Id));

        Assert.Equal(JobState.Failed, (await fixture.Store.GetAsync(job.Id))?.State);
        Assert.Empty(await fixture.Store.GetPagesAsync(job.Id));
    }

    private static PluginOutcome<RecognitionResult> Success(PageArtifact page) =>
        new PluginOutcome<RecognitionResult>.Succeeded(new RecognitionResult(
            [new SpatialTextBlock($"recognized {Path.GetFileName(page.SourcePath)}", [], 1, "test")]));

    private sealed class ScriptedRecognizer(
        RecognizerIdentity? identity = null) : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = identity
            ?? new RecognizerIdentity("org.ocrworkbench.test", "1.0.0");
        public List<string> RecognizedPaths { get; } = [];
        public Func<int, Task<bool>>? AfterRecognitionAsync { get; set; }
        public Func<int, PageArtifact, PluginOutcome<RecognitionResult>> OutcomeFactory { get; set; } =
            (_, page) => Success(page);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            RecognizedPaths.Add(page.SourcePath);
            var call = RecognizedPaths.Count;
            if (AfterRecognitionAsync is not null)
            {
                await AfterRecognitionAsync(call);
            }

            return OutcomeFactory(call, page);
        }
    }

    private sealed class CheckpointFixture : IAsyncDisposable
    {
        private CheckpointFixture(
            string directory,
            string databasePath,
            string outputPath,
            IReadOnlyList<string> inputPaths,
            SqliteJobStore store)
        {
            Directory = directory;
            DatabasePath = databasePath;
            OutputPath = outputPath;
            InputPaths = inputPaths;
            Store = store;
            Spec = new JobSpec(
                inputPaths.Select(path => new ImageInput(path, "image/png")).ToArray(),
                new RecognitionOptions(),
                new PlainTextExportOptions(outputPath));
        }

        public string Directory { get; }
        public string DatabasePath { get; }
        public string OutputPath { get; }
        public IReadOnlyList<string> InputPaths { get; }
        public SqliteJobStore Store { get; }
        public JobSpec Spec { get; }

        public static async Task<CheckpointFixture> CreateAsync(int inputCount)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"neocr-checkpoint-test-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var inputs = new List<string>(inputCount);
            for (var index = 0; index < inputCount; index++)
            {
                var input = Path.Combine(directory, $"input-{index + 1}.png");
                await File.WriteAllBytesAsync(input, [(byte)(index + 1)]);
                inputs.Add(input);
            }

            var databasePath = Path.Combine(directory, "jobs.db");
            var store = new SqliteJobStore(databasePath);
            await store.InitializeAsync();
            return new CheckpointFixture(
                directory,
                databasePath,
                Path.Combine(directory, "output.txt"),
                inputs,
                store);
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
