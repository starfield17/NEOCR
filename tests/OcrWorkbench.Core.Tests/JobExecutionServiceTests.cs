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
            Assert.Empty(store.Pages);
            Assert.Equal("existing output", await File.ReadAllTextAsync(output));
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

}
