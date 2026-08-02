using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed class OcrPipeline(IRecognizer recognizer)
{
    public async Task<PipelineResult> RunAsync(JobSpec job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Inputs.Count == 0)
        {
            throw new ArgumentException("A job must contain at least one input.", nameof(job));
        }

        var completed = new List<PageRecognition>(job.Inputs.Count);
        var declined = new List<PageDecline>();

        for (var inputIndex = 0; inputIndex < job.Inputs.Count; inputIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = PageArtifactFactory.Create(job.Inputs[inputIndex], inputIndex);
            var outcome = await recognizer.RecognizeAsync(page, job.Recognition, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case PluginOutcome<RecognitionResult>.Succeeded success:
                    completed.Add(new PageRecognition(page, success.Value));
                    break;
                case PluginOutcome<RecognitionResult>.Declined refusal:
                    declined.Add(new PageDecline(page, refusal.ReasonCode, refusal.Message));
                    break;
                case PluginOutcome<RecognitionResult>.Failed failure:
                    throw new RecognitionException(failure.ErrorCode, failure.Message, failure.Retryable);
            }
        }

        await PlainTextExporter.WriteAtomicallyAsync(
            job.Export.OutputPath,
            completed,
            cancellationToken).ConfigureAwait(false);

        return new PipelineResult(completed, declined, Path.GetFullPath(job.Export.OutputPath));
    }
}

public sealed record PageDecline(PageArtifact Page, string ReasonCode, string Message);

public sealed record PipelineResult(
    IReadOnlyList<PageRecognition> Completed,
    IReadOnlyList<PageDecline> Declined,
    string OutputPath);

public sealed class RecognitionException(string code, string message, bool retryable) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
