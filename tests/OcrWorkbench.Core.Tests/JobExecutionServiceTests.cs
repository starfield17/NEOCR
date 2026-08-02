using OcrWorkbench.Contracts;
using OcrWorkbench.Infrastructure;

namespace OcrWorkbench.Core.Tests;

public sealed class JobExecutionServiceTests
{
    [Fact]
    public async Task Caller_cancellation_marks_running_job_cancelled_and_preserves_output()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neocr-execution-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.png");
            var output = Path.Combine(directory, "output.txt");
            await File.WriteAllBytesAsync(input, [1]);
            await File.WriteAllTextAsync(output, "existing output");
            var store = new MemoryJobStore();
            await using var recognizer = new BlockingRecognizer();
            using var cancellation = new CancellationTokenSource();
            var execution = new JobExecutionService(store, recognizer).ExecuteAsync(
                new JobSpec(
                    [new ImageInput(input, "image/png")],
                    new RecognitionOptions(),
                    new PlainTextExportOptions(output)),
                cancellation.Token);
            await recognizer.Started.Task;

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            Assert.Equal(JobState.Cancelled, Assert.Single(store.Jobs.Values).State);
            Assert.Empty(store.Pages);
            Assert.Equal("existing output", await File.ReadAllTextAsync(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Lease_loss_stops_work_without_marking_the_job_failed_or_cancelled()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neocr-lease-loss-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.png");
            await File.WriteAllBytesAsync(input, [1]);
            var store = new MemoryJobStore { RejectLeaseRenewals = true };
            await using var recognizer = new BlockingRecognizer();
            var execution = new JobExecutionService(
                store,
                recognizer,
                leaseDuration: TimeSpan.FromSeconds(1),
                heartbeatInterval: TimeSpan.FromMilliseconds(20)).ExecuteAsync(
                    new JobSpec(
                        [new ImageInput(input, "image/png")],
                        new RecognitionOptions(),
                        new PlainTextExportOptions(Path.Combine(directory, "output.txt"))));
            await recognizer.Started.Task;

            var exception = await Assert.ThrowsAsync<JobLeaseLostException>(() => execution);

            var job = Assert.Single(store.Jobs.Values);
            Assert.Equal(job.Id, exception.JobId);
            Assert.Equal(JobState.Running, job.State);
            Assert.NotNull(job.RunId);
            Assert.NotEmpty(store.Pages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Throwing_cancellation_callback_does_not_mask_lease_loss()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neocr-lease-callback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.png");
            await File.WriteAllBytesAsync(input, [1]);
            var store = new MemoryJobStore { RejectLeaseRenewals = true };
            await using var recognizer = new ThrowingCancellationRecognizer();
            var execution = new JobExecutionService(
                store,
                recognizer,
                leaseDuration: TimeSpan.FromSeconds(1),
                heartbeatInterval: TimeSpan.FromMilliseconds(20)).ExecuteAsync(
                    new JobSpec(
                        [new ImageInput(input, "image/png")],
                        new RecognitionOptions(),
                        new PlainTextExportOptions(Path.Combine(directory, "output.txt"))));
            await recognizer.Started.Task;

            await Assert.ThrowsAsync<JobLeaseLostException>(() => execution);
            Assert.Equal(JobState.Running, Assert.Single(store.Jobs.Values).State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_heartbeat_does_not_report_lease_loss_when_run_finishes_or_pauses(
        bool requestPause)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"neocr-heartbeat-race-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputs = new List<ImageInput>();
            for (var index = 0; index < (requestPause ? 2 : 1); index++)
            {
                var input = Path.Combine(directory, $"input-{index}.png");
                await File.WriteAllBytesAsync(input, [(byte)index]);
                inputs.Add(new ImageInput(input, "image/png"));
            }

            var store = new SqliteJobStore(Path.Combine(directory, "jobs.db"));
            await store.InitializeAsync();
            var recognizer = new DelayedRecognizer();
            var service = new JobExecutionService(
                store,
                recognizer,
                leaseDuration: TimeSpan.FromSeconds(2),
                heartbeatInterval: TimeSpan.FromMilliseconds(10));
            var job = await service.SubmitAsync(new JobSpec(
                inputs,
                new RecognitionOptions(),
                new PlainTextExportOptions(Path.Combine(directory, "output.txt"))));
            if (requestPause)
            {
                recognizer.AfterRecognitionAsync = () => service.RequestPauseAsync(job.Id);
            }

            var result = await service.RunAsync(job.Id);

            if (requestPause)
            {
                Assert.IsType<JobExecutionResult.Paused>(result);
                Assert.Equal(JobState.Paused, (await store.GetAsync(job.Id))?.State);
            }
            else
            {
                Assert.IsType<JobExecutionResult.Finished>(result);
                Assert.Equal(JobState.Completed, (await store.GetAsync(job.Id))?.State);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BlockingRecognizer : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class DelayedRecognizer : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public Func<Task<bool>>? AfterRecognitionAsync { get; set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            if (AfterRecognitionAsync is not null)
            {
                await AfterRecognitionAsync();
                AfterRecognitionAsync = null;
            }

            return new PluginOutcome<RecognitionResult>.Succeeded(new RecognitionResult([]));
        }
    }

    private sealed class ThrowingCancellationRecognizer : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(
                () => throw new InvalidOperationException("callback failure"));
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

}
