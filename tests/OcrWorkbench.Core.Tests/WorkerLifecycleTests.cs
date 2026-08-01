using OcrWorkbench.Contracts;
using OcrWorkbench.PluginHost;

namespace OcrWorkbench.Core.Tests;

public sealed class WorkerLifecycleTests
{
    [Fact]
    public async Task Session_reuses_worker_after_successful_requests()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();

        var firstProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);
        var secondProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        Assert.Equal(firstProcess, secondProcess);
    }

    [Fact]
    public async Task Cooperative_cancellation_drains_responses_and_preserves_worker()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();
        var originalProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RecognizeAsync(
            CreatePage(fixture.InputPath),
            new RecognitionOptions("x-test-wait-for-cancel"),
            cancellation.Token).AsTask());
        var nextProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        Assert.Equal(originalProcess, nextProcess);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_does_not_start_or_poison_worker()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RecognizeAsync(
            CreatePage(fixture.InputPath),
            new RecognitionOptions(),
            cancellation.Token).AsTask());

        Assert.StartsWith("fake:", await RecognizeProcessIdAsync(session, fixture.InputPath));
    }

    [Fact]
    public async Task Uncooperative_cancellation_kills_worker_and_next_request_restarts_it()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();
        var originalProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RecognizeAsync(
            CreatePage(fixture.InputPath),
            new RecognitionOptions("x-test-ignore-cancel"),
            cancellation.Token).AsTask());
        var nextProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        Assert.NotEqual(originalProcess, nextProcess);
    }

    [Fact]
    public async Task Worker_crash_fails_current_request_without_retry_and_restarts_later()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();
        var originalProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        await Assert.ThrowsAnyAsync<Exception>(() => session.RecognizeAsync(
            CreatePage(fixture.InputPath),
            new RecognitionOptions("x-test-crash")).AsTask());
        var nextProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        Assert.NotEqual(originalProcess, nextProcess);
    }

    [Fact]
    public async Task Unsolicited_correlation_faults_worker_and_next_request_restarts_it()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        await using var session = fixture.Package.CreateRecognizerSession();
        var originalProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        await Assert.ThrowsAnyAsync<Exception>(() => session.RecognizeAsync(
            CreatePage(fixture.InputPath),
            new RecognitionOptions("x-test-wrong-correlation")).AsTask());
        var nextProcess = await RecognizeProcessIdAsync(session, fixture.InputPath);

        Assert.NotEqual(originalProcess, nextProcess);
    }

    private static async Task<string> RecognizeProcessIdAsync(IRecognizer recognizer, string inputPath)
    {
        var outcome = await recognizer.RecognizeAsync(CreatePage(inputPath), new RecognitionOptions());
        var success = Assert.IsType<PluginOutcome<RecognitionResult>.Succeeded>(outcome);
        return Assert.Single(success.Value.SpatialBlocks).Source;
    }

    private static PageArtifact CreatePage(string inputPath) => new(
        "test-page",
        inputPath,
        "image/png");

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private WorkerFixture(string directory, string inputPath, PluginPackage package)
        {
            Directory = directory;
            InputPath = inputPath;
            Package = package;
        }

        public string Directory { get; }
        public string InputPath { get; }
        public PluginPackage Package { get; }

        public static async Task<WorkerFixture> CreateAsync()
        {
            var root = FindRepositoryRoot();
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
            var packageDirectory = Path.Combine(
                root,
                "workers",
                "OcrWorkbench.FakeWorker",
                "bin",
                configuration,
                "net10.0");
            var directory = Path.Combine(Path.GetTempPath(), $"neocr-worker-test-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var inputPath = Path.Combine(directory, "input.png");
            await File.WriteAllBytesAsync(inputPath, [1, 2, 3]);
            return new WorkerFixture(directory, inputPath, await PluginPackage.LoadAsync(packageDirectory));
        }

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OcrWorkbench.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository root.");
        }
    }
}
