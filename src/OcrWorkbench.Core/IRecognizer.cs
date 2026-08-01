using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public interface IRecognizer : IAsyncDisposable
{
    ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
        PageArtifact page,
        RecognitionOptions options,
        CancellationToken cancellationToken = default);
}

