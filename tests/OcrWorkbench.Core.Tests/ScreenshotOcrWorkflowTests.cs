using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core.Tests;

public sealed class ScreenshotOcrWorkflowTests
{
    [Fact]
    public async Task Successful_capture_uses_normal_job_pipeline_and_removes_temporary_artifacts()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new MemoryJobStore();
            await using var recognizer = new SuccessfulRecognizer();
            var workflow = new ScreenshotOcrWorkflow(
                new FixedScreenshotService(new ScreenshotCaptureResult.Succeeded([1, 2, 3], 30, 20)),
                store,
                recognizer,
                root);

            var result = Assert.IsType<ScreenshotOcrResult.Succeeded>(await workflow.RunAsync());

            Assert.Equal(JobState.Completed, result.State);
            Assert.Contains("recognized capture.png", result.Text);
            Assert.Single(store.Jobs);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_capture_does_not_create_a_job()
    {
        var store = new MemoryJobStore();
        await using var recognizer = new SuccessfulRecognizer();
        var workflow = new ScreenshotOcrWorkflow(
            new FixedScreenshotService(new ScreenshotCaptureResult.Cancelled()),
            store,
            recognizer);

        Assert.IsType<ScreenshotOcrResult.Cancelled>(await workflow.RunAsync());
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task Capture_failure_is_preserved_without_creating_a_job()
    {
        var store = new MemoryJobStore();
        await using var recognizer = new SuccessfulRecognizer();
        var workflow = new ScreenshotOcrWorkflow(
            new FixedScreenshotService(new ScreenshotCaptureResult.Failed(
                ScreenshotCaptureFailure.PermissionDenied,
                "permission required")),
            store,
            recognizer);

        var result = Assert.IsType<ScreenshotOcrResult.Failed>(await workflow.RunAsync());
        Assert.Equal(ScreenshotCaptureFailure.PermissionDenied, result.Failure);
        Assert.Empty(store.Jobs);
    }

    [Fact]
    public async Task Concurrent_capture_is_rejected_as_busy()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var screenshot = new BlockingScreenshotService();
            var store = new MemoryJobStore();
            await using var recognizer = new SuccessfulRecognizer();
            var workflow = new ScreenshotOcrWorkflow(screenshot, store, recognizer, root);

            var first = workflow.RunAsync();
            await screenshot.Started.Task;
            Assert.IsType<ScreenshotOcrResult.Busy>(await workflow.RunAsync());

            screenshot.Complete(new ScreenshotCaptureResult.Cancelled());
            Assert.IsType<ScreenshotOcrResult.Cancelled>(await first);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Temporary_artifacts_are_removed_when_recognition_fails()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var store = new MemoryJobStore();
            await using var recognizer = new FailingRecognizer();
            var workflow = new ScreenshotOcrWorkflow(
                new FixedScreenshotService(new ScreenshotCaptureResult.Succeeded([1], 1, 1)),
                store,
                recognizer,
                root);

            await Assert.ThrowsAsync<RecognitionException>(() => workflow.RunAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capture_completed_callback_runs_before_recognition()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var callbackCompleted = false;
            var store = new MemoryJobStore();
            await using var recognizer = new CallbackCheckingRecognizer(() => callbackCompleted);
            var workflow = new ScreenshotOcrWorkflow(
                new FixedScreenshotService(new ScreenshotCaptureResult.Succeeded([1], 1, 1)),
                store,
                recognizer,
                root);

            await workflow.RunAsync(onCaptureCompleted: () =>
            {
                callbackCompleted = true;
                return ValueTask.CompletedTask;
            });

            Assert.True(callbackCompleted);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"neocr-screenshot-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedScreenshotService(ScreenshotCaptureResult result) : IInteractiveScreenshotService
    {
        public Task<ScreenshotCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class BlockingScreenshotService : IInteractiveScreenshotService
    {
        private readonly TaskCompletionSource<ScreenshotCaptureResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ScreenshotCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return _completion.Task;
        }

        public void Complete(ScreenshotCaptureResult result) => _completion.SetResult(result);
    }

    private sealed class SuccessfulRecognizer : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Assert.True(File.Exists(page.SourcePath));
            PluginOutcome<RecognitionResult> result = new PluginOutcome<RecognitionResult>.Succeeded(
                new RecognitionResult([new SpatialTextBlock("recognized capture.png", [], 1, "test")]));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingRecognizer : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            PluginOutcome<RecognitionResult> result = new PluginOutcome<RecognitionResult>.Failed("Test.Failed", "failure", false);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CallbackCheckingRecognizer(Func<bool> callbackCompleted) : IRecognizer
    {
        public RecognizerIdentity Identity { get; } = new("org.ocrworkbench.test", "1.0.0");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            Assert.True(callbackCompleted());
            PluginOutcome<RecognitionResult> result = new PluginOutcome<RecognitionResult>.Succeeded(
                new RecognitionResult([new SpatialTextBlock("recognized capture.png", [], 1, "test")]));
            return ValueTask.FromResult(result);
        }
    }

}
