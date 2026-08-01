using OcrWorkbench.Contracts;

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
            Assert.Equal("existing output", await File.ReadAllTextAsync(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class BlockingRecognizer : IRecognizer
    {
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

    private sealed class MemoryJobStore : IJobStore
    {
        public Dictionary<Guid, JobRecord> Jobs { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JobRecord> EnqueueAsync(JobSpec spec, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var record = new JobRecord(Guid.NewGuid(), spec, JobState.Queued, now, now);
            Jobs.Add(record.Id, record);
            return Task.FromResult(record);
        }

        public Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Jobs.GetValueOrDefault(id));

        public Task<bool> TransitionAsync(
            Guid id,
            JobState expected,
            JobState target,
            string? errorCode = null,
            string? errorMessage = null,
            CancellationToken cancellationToken = default)
        {
            if (!Jobs.TryGetValue(id, out var record) || record.State != expected)
            {
                return Task.FromResult(false);
            }

            Jobs[id] = record with
            {
                State = target,
                UpdatedAt = DateTimeOffset.UtcNow,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
            };
            return Task.FromResult(true);
        }
    }
}
