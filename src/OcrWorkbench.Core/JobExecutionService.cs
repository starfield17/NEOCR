using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed class JobExecutionService(IJobStore store, IRecognizer recognizer)
{
    public async Task<JobExecutionResult> ExecuteAsync(
        JobSpec spec,
        CancellationToken cancellationToken = default)
    {
        var job = await store.EnqueueAsync(spec, cancellationToken).ConfigureAwait(false);
        if (!await store.TransitionAsync(
            job.Id,
            JobState.Queued,
            JobState.Running,
            cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Job '{job.Id}' could not be claimed.");
        }

        try
        {
            var result = await new OcrPipeline(recognizer)
                .RunAsync(spec, cancellationToken)
                .ConfigureAwait(false);
            var finalState = result.Declined.Count == 0
                ? JobState.Completed
                : JobState.CompletedWithErrors;
            if (!await store.TransitionAsync(
                job.Id,
                JobState.Running,
                finalState,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Job '{job.Id}' completion could not be persisted.");
            }

            return new JobExecutionResult(job.Id, finalState, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.TransitionAsync(
                job.Id,
                JobState.Running,
                JobState.Cancelled,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception is RecognitionException recognition
                ? recognition.Code
                : "Host.Unhandled";
            await store.TransitionAsync(
                job.Id,
                JobState.Running,
                JobState.Failed,
                errorCode,
                exception.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

public sealed record JobExecutionResult(Guid JobId, JobState State, PipelineResult Pipeline);
