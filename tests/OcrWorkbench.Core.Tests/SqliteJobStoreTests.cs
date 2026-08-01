using OcrWorkbench.Contracts;
using OcrWorkbench.Infrastructure;

namespace OcrWorkbench.Core.Tests;

public sealed class SqliteJobStoreTests
{
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

            var queued = await store.EnqueueAsync(spec);
            Assert.True(await store.TransitionAsync(queued.Id, JobState.Queued, JobState.Running));
            Assert.False(await store.TransitionAsync(queued.Id, JobState.Queued, JobState.Cancelled));

            var running = await store.GetAsync(queued.Id);
            Assert.NotNull(running);
            Assert.Equal(JobState.Running, running.State);
            Assert.Equal("zh-Hans", running.Spec.Recognition.Language);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

