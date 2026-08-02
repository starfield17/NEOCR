using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public interface IRecognizer : IAsyncDisposable
{
    RecognizerIdentity Identity { get; }

    ValueTask<PluginOutcome<RecognitionResult>> RecognizeAsync(
        PageArtifact page,
        RecognitionOptions options,
        CancellationToken cancellationToken = default);
}
