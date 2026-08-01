using System.Security.Cryptography;
using System.Text;
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

        foreach (var input in job.Inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(input.Path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Input image does not exist.", fullPath);
            }

            var page = new PageArtifact(CreateStableId(fullPath), fullPath, input.MimeType);
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

    private static string CreateStableId(string fullPath)
    {
        var file = new FileInfo(fullPath);
        var identity = $"{fullPath}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
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

