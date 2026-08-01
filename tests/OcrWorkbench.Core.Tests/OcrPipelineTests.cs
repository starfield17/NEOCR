using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core.Tests;

public sealed class OcrPipelineTests
{
    [Fact]
    public async Task ExportsSuccessfulPagesAndReportsDeclines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "first.png");
            var second = Path.Combine(directory, "second.png");
            await File.WriteAllBytesAsync(first, [1]);
            await File.WriteAllBytesAsync(second, [2]);
            var output = Path.Combine(directory, "result.txt");

            await using var recognizer = new StubRecognizer(second);
            var pipeline = new OcrPipeline(recognizer);
            var result = await pipeline.RunAsync(new JobSpec(
                [new ImageInput(first), new ImageInput(second)],
                new RecognitionOptions(),
                new PlainTextExportOptions(output)));

            Assert.Single(result.Completed);
            Assert.Single(result.Declined);
            Assert.Equal(DeclineReasonCodes.CapabilityMissing, result.Declined[0].ReasonCode);
            Assert.Contains("recognized first.png", await File.ReadAllTextAsync(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StubRecognizer(string declinedPath) : IRecognizer
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
            PageArtifact page,
            RecognitionOptions options,
            CancellationToken cancellationToken = default)
        {
            PluginOutcome<RecognitionResult> outcome = page.SourcePath == declinedPath
                ? new PluginOutcome<RecognitionResult>.Declined(
                    DeclineReasonCodes.CapabilityMissing,
                    "declined for test",
                    false,
                    [])
                : new PluginOutcome<RecognitionResult>.Succeeded(new RecognitionResult(
                    [new SpatialTextBlock(
                        $"recognized {Path.GetFileName(page.SourcePath)}",
                        [],
                        1,
                        "test")]));
            return ValueTask.FromResult(outcome);
        }
    }
}

