using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed class JobExecutionService(IJobStore store, IRecognizer recognizer)
{
    public Task<JobRecord> SubmitAsync(JobSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Inputs.Count == 0)
        {
            throw new ArgumentException("A job must contain at least one input.", nameof(spec));
        }

        return store.EnqueueAsync(spec, recognizer.Identity, cancellationToken);
    }

    public async Task<JobExecutionResult> ExecuteAsync(
        JobSpec spec,
        CancellationToken cancellationToken = default)
    {
        var job = await SubmitAsync(spec, cancellationToken).ConfigureAwait(false);
        return await RunAsync(job.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobExecutionResult> ResumeAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        EnsureRecognizerMatches(job);
        if (job.State != JobState.Paused
            || !await store.TransitionAsync(
                jobId,
                JobState.Paused,
                JobState.Queued,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Job '{jobId}' is not paused or could not be queued for resume.");
        }

        return await RunAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobExecutionResult> RunAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        EnsureRecognizerMatches(job);
        if (job.State != JobState.Queued
            || !await store.TransitionAsync(
                job.Id,
                JobState.Queued,
                JobState.Running,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Job '{job.Id}' could not be claimed.");
        }

        try
        {
            var checkpoints = await PreparePagesAsync(job, cancellationToken).ConfigureAwait(false);
            var completed = new List<PageRecognition>(checkpoints.Count);
            var declined = new List<PageDecline>();

            foreach (var checkpoint in checkpoints)
            {
                if (checkpoint.State == PageCheckpointState.Succeeded)
                {
                    completed.Add(new PageRecognition(
                        checkpoint.Page,
                        checkpoint.Result ?? throw InvalidCheckpoint(job.Id, checkpoint.InputIndex)));
                    continue;
                }

                if (checkpoint.State == PageCheckpointState.Declined)
                {
                    declined.Add(new PageDecline(
                        checkpoint.Page,
                        checkpoint.DeclineReasonCode ?? throw InvalidCheckpoint(job.Id, checkpoint.InputIndex),
                        checkpoint.DeclineMessage ?? string.Empty));
                    continue;
                }

                if (await PauseAtBoundaryAsync(job.Id, completed.Count + declined.Count, checkpoints.Count)
                    .ConfigureAwait(false) is { } paused)
                {
                    return paused;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await recognizer
                    .RecognizeAsync(checkpoint.Page, job.Spec.Recognition, cancellationToken)
                    .ConfigureAwait(false);
                switch (outcome)
                {
                    case PluginOutcome<RecognitionResult>.Succeeded success:
                        if (!await store.SavePageSucceededAsync(
                            job.Id,
                            checkpoint.InputIndex,
                            checkpoint.Page.StableId,
                            success.Value,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Page checkpoint {checkpoint.InputIndex} for job '{job.Id}' could not be persisted.");
                        }

                        completed.Add(new PageRecognition(checkpoint.Page, success.Value));
                        break;
                    case PluginOutcome<RecognitionResult>.Declined refusal:
                        if (!await store.SavePageDeclinedAsync(
                            job.Id,
                            checkpoint.InputIndex,
                            checkpoint.Page.StableId,
                            refusal.ReasonCode,
                            refusal.Message,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException(
                                $"Page checkpoint {checkpoint.InputIndex} for job '{job.Id}' could not be persisted.");
                        }

                        declined.Add(new PageDecline(checkpoint.Page, refusal.ReasonCode, refusal.Message));
                        break;
                    case PluginOutcome<RecognitionResult>.Failed failure:
                        throw new RecognitionException(failure.ErrorCode, failure.Message, failure.Retryable);
                }
            }

            await PlainTextExporter.WriteAtomicallyAsync(
                job.Spec.Export.OutputPath,
                completed.OrderBy(page => page.Page.PageNumber).ToArray(),
                cancellationToken).ConfigureAwait(false);
            var finalState = declined.Count == 0
                ? JobState.Completed
                : JobState.CompletedWithErrors;
            await FinalizeAsync(job.Id, finalState).ConfigureAwait(false);
            var pipeline = new PipelineResult(
                completed.OrderBy(page => page.Page.PageNumber).ToArray(),
                declined.OrderBy(page => page.Page.PageNumber).ToArray(),
                Path.GetFullPath(job.Spec.Export.OutputPath));
            return new JobExecutionResult.Finished(job.Id, finalState, pipeline);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinalizeAsync(job.Id, JobState.Cancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception switch
            {
                RecognitionException recognition => recognition.Code,
                JobInputChangedException => "Input.Changed",
                _ => "Host.Unhandled",
            };
            await FinalizeAsync(job.Id, JobState.Failed, errorCode, exception.Message).ConfigureAwait(false);
            throw;
        }
    }

    public Task<bool> RequestPauseAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        store.TransitionAsync(
            jobId,
            JobState.Running,
            JobState.Pausing,
            cancellationToken: cancellationToken);

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await GetRequiredAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job.State switch
        {
            JobState.Queued => await store.TransitionAsync(
                jobId,
                JobState.Queued,
                JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            JobState.Paused => await store.TransitionAsync(
                jobId,
                JobState.Paused,
                JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<IReadOnlyList<PageCheckpoint>> PreparePagesAsync(
        JobRecord job,
        CancellationToken cancellationToken)
    {
        PageArtifact[] snapshots;
        try
        {
            snapshots = job.Spec.Inputs
                .Select(PageArtifactFactory.Create)
                .ToArray();
        }
        catch (FileNotFoundException exception)
        {
            throw new JobInputChangedException(exception.Message, exception);
        }

        var checkpoints = await store.GetPagesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (checkpoints.Count == 0)
        {
            await store.InitializePagesAsync(job.Id, snapshots, CancellationToken.None).ConfigureAwait(false);
            return await store.GetPagesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }

        if (checkpoints.Count != snapshots.Length)
        {
            throw new JobInputChangedException("The number of job inputs no longer matches its page checkpoint.");
        }

        for (var index = 0; index < snapshots.Length; index++)
        {
            var stored = checkpoints[index];
            var current = snapshots[index];
            if (stored.InputIndex != index
                || !string.Equals(stored.Page.StableId, current.StableId, StringComparison.Ordinal)
                || !string.Equals(stored.Page.SourcePath, current.SourcePath, PathComparison)
                || !string.Equals(stored.Page.MimeType, current.MimeType, StringComparison.Ordinal)
                || stored.Page.PageNumber != current.PageNumber)
            {
                throw new JobInputChangedException(
                    $"Input {index + 1} changed after the job checkpoint was created.");
            }
        }

        return checkpoints;
    }

    private async Task<JobExecutionResult.Paused?> PauseAtBoundaryAsync(
        Guid jobId,
        int completedPages,
        int totalPages)
    {
        var job = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (job.State == JobState.Running)
        {
            return null;
        }

        if (job.State == JobState.Pausing
            && await store.TransitionAsync(
                jobId,
                JobState.Pausing,
                JobState.Paused,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            return new JobExecutionResult.Paused(jobId, completedPages, totalPages);
        }

        throw new InvalidOperationException($"Job '{jobId}' left the running state unexpectedly.");
    }

    private async Task FinalizeAsync(
        Guid jobId,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var current = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (current.State == target)
        {
            return;
        }

        if (current.State is not (JobState.Running or JobState.Pausing)
            || !await store.TransitionAsync(
                jobId,
                current.State,
                target,
                errorCode,
                errorMessage,
                CancellationToken.None).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Job '{jobId}' could not transition from {current.State} to {target}.");
        }
    }

    private async Task<JobRecord> GetRequiredAsync(Guid jobId, CancellationToken cancellationToken) =>
        await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");

    private void EnsureRecognizerMatches(JobRecord job)
    {
        if (job.Recognizer is null
            || !string.Equals(job.Recognizer.Id, recognizer.Identity.Id, StringComparison.Ordinal)
            || !string.Equals(job.Recognizer.Version, recognizer.Identity.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Job '{job.Id}' requires recognizer '{job.Recognizer?.Id}@{job.Recognizer?.Version}', but '{recognizer.Identity.Id}@{recognizer.Identity.Version}' is active.");
        }
    }

    private static InvalidDataException InvalidCheckpoint(Guid jobId, int inputIndex) =>
        new($"Page checkpoint {inputIndex} for job '{jobId}' is incomplete.");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

public abstract record JobExecutionResult
{
    private JobExecutionResult() { }

    public sealed record Finished(Guid JobId, JobState State, PipelineResult Pipeline) : JobExecutionResult;

    public sealed record Paused(Guid JobId, int CompletedPages, int TotalPages) : JobExecutionResult;
}

public sealed class JobInputChangedException(string message, Exception? innerException = null)
    : Exception(message, innerException);
