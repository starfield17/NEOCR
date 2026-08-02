using OcrWorkbench.Contracts;
using System.Collections.Concurrent;

namespace OcrWorkbench.Core;

public sealed class JobExecutionService
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(5);

    private readonly IJobStore _store;
    private readonly IRecognizer _recognizer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _heartbeatInterval;
    private readonly ConcurrentDictionary<Guid, Guid> _activeRuns = new();

    public JobExecutionService(
        IJobStore store,
        IRecognizer recognizer,
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? heartbeatInterval = null)
    {
        _store = store;
        _recognizer = recognizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        if (_leaseDuration <= TimeSpan.Zero
            || _heartbeatInterval <= TimeSpan.Zero
            || _heartbeatInterval >= _leaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                "The heartbeat interval must be positive and shorter than the lease duration.");
        }
    }

    public Task<JobRecord> SubmitAsync(JobSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Inputs.Count == 0)
        {
            throw new ArgumentException("A job must contain at least one input.", nameof(spec));
        }

        return _store.EnqueueAsync(spec, _recognizer.Identity, cancellationToken);
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
            || !await _store.TransitionAsync(
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
        var runId = Guid.NewGuid();
        var claimTime = _timeProvider.GetUtcNow();
        if (job.State != JobState.Queued
            || await _store.TryClaimAsync(
                job.Id,
                runId,
                claimTime,
                claimTime + _leaseDuration,
                CancellationToken.None).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException($"Job '{job.Id}' could not be claimed.");
        }

        if (!_activeRuns.TryAdd(job.Id, runId))
        {
            throw new InvalidOperationException($"Job '{job.Id}' is already running in this service.");
        }

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var heartbeat = new LeaseHeartbeat(
            _store,
            job.Id,
            runId,
            _timeProvider,
            _leaseDuration,
            _heartbeatInterval,
            executionCancellation);
        try
        {
            var checkpoints = await PreparePagesAsync(
                job, runId, executionCancellation.Token).ConfigureAwait(false);
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

                if (await PauseAtBoundaryAsync(
                    job.Id, runId, completed.Count + declined.Count, checkpoints.Count)
                    .ConfigureAwait(false) is { } paused)
                {
                    return paused;
                }

                executionCancellation.Token.ThrowIfCancellationRequested();
                var outcome = await _recognizer
                    .RecognizeAsync(checkpoint.Page, job.Spec.Recognition, executionCancellation.Token)
                    .ConfigureAwait(false);
                switch (outcome)
                {
                    case PluginOutcome<RecognitionResult>.Succeeded success:
                        if (!await _store.SavePageSucceededAsync(
                            job.Id,
                            runId,
                            _timeProvider.GetUtcNow(),
                            checkpoint.InputIndex,
                            checkpoint.Page.StableId,
                            success.Value,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            throw LeaseLost(job.Id, runId);
                        }

                        completed.Add(new PageRecognition(checkpoint.Page, success.Value));
                        break;
                    case PluginOutcome<RecognitionResult>.Declined refusal:
                        if (!await _store.SavePageDeclinedAsync(
                            job.Id,
                            runId,
                            _timeProvider.GetUtcNow(),
                            checkpoint.InputIndex,
                            checkpoint.Page.StableId,
                            refusal.ReasonCode,
                            refusal.Message,
                            CancellationToken.None).ConfigureAwait(false))
                        {
                            throw LeaseLost(job.Id, runId);
                        }

                        declined.Add(new PageDecline(checkpoint.Page, refusal.ReasonCode, refusal.Message));
                        break;
                    case PluginOutcome<RecognitionResult>.Failed failure:
                        throw new RecognitionException(failure.ErrorCode, failure.Message, failure.Retryable);
                }
            }

            var finalState = declined.Count == 0
                ? JobState.Completed
                : JobState.CompletedWithErrors;
            var exportTime = _timeProvider.GetUtcNow();
            if (!await _store.RenewLeaseAsync(
                job.Id,
                runId,
                exportTime,
                exportTime + _leaseDuration,
                CancellationToken.None).ConfigureAwait(false))
            {
                throw LeaseLost(job.Id, runId);
            }

            await PlainTextExporter.WriteAtomicallyAsync(
                job.Spec.Export.OutputPath,
                completed.OrderBy(page => page.Page.PageNumber).ToArray(),
                executionCancellation.Token).ConfigureAwait(false);
            await FinalizeAsync(job.Id, runId, finalState).ConfigureAwait(false);

            var pipeline = new PipelineResult(
                completed.OrderBy(page => page.Page.PageNumber).ToArray(),
                declined.OrderBy(page => page.Page.PageNumber).ToArray(),
                Path.GetFullPath(job.Spec.Export.OutputPath));
            return new JobExecutionResult.Finished(job.Id, finalState, pipeline);
        }
        catch (OperationCanceledException) when (heartbeat.LeaseLost)
        {
            throw heartbeat.CreateLeaseLostException();
        }
        catch (JobLeaseLostException)
        {
            throw;
        }
        catch (Exception) when (heartbeat.LeaseLost)
        {
            throw heartbeat.CreateLeaseLostException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinalizeAsync(job.Id, runId, JobState.Cancelled).ConfigureAwait(false);
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
            await FinalizeAsync(
                job.Id, runId, JobState.Failed, errorCode, exception.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _activeRuns.TryRemove(new KeyValuePair<Guid, Guid>(job.Id, runId));
        }
    }

    public Task<bool> RequestPauseAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _activeRuns.TryGetValue(jobId, out var runId)
            ? _store.TransitionOwnedAsync(
                jobId,
                runId,
                _timeProvider.GetUtcNow(),
                JobState.Running,
                JobState.Pausing,
                cancellationToken: cancellationToken)
            : Task.FromResult(false);

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await GetRequiredAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job.State switch
        {
            JobState.Queued => await _store.TransitionAsync(
                jobId,
                JobState.Queued,
                JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            JobState.Paused => await _store.TransitionAsync(
                jobId,
                JobState.Paused,
                JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<IReadOnlyList<PageCheckpoint>> PreparePagesAsync(
        JobRecord job,
        Guid runId,
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

        var checkpoints = await _store.GetPagesAsync(job.Id, cancellationToken).ConfigureAwait(false);
        if (checkpoints.Count == 0)
        {
            await _store.InitializePagesAsync(
                job.Id,
                runId,
                _timeProvider.GetUtcNow(),
                snapshots,
                CancellationToken.None).ConfigureAwait(false);
            return await _store.GetPagesAsync(job.Id, cancellationToken).ConfigureAwait(false);
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
        Guid runId,
        int completedPages,
        int totalPages)
    {
        var job = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (job.State == JobState.Running)
        {
            return null;
        }

        if (job.State == JobState.Pausing
            && await _store.TransitionOwnedAsync(
                jobId,
                runId,
                _timeProvider.GetUtcNow(),
                JobState.Pausing,
                JobState.Paused,
                cancellationToken: CancellationToken.None).ConfigureAwait(false))
        {
            return new JobExecutionResult.Paused(jobId, completedPages, totalPages);
        }

        throw LeaseLost(jobId, runId);
    }

    private async Task FinalizeAsync(
        Guid jobId,
        Guid runId,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null)
    {
        var current = await GetRequiredAsync(jobId, CancellationToken.None).ConfigureAwait(false);
        if (current.State is not (JobState.Running or JobState.Pausing)
            || !await _store.TransitionOwnedAsync(
                jobId,
                runId,
                _timeProvider.GetUtcNow(),
                current.State,
                target,
                errorCode,
                errorMessage,
                CancellationToken.None).ConfigureAwait(false))
        {
            throw LeaseLost(jobId, runId);
        }
    }

    private async Task<JobRecord> GetRequiredAsync(Guid jobId, CancellationToken cancellationToken) =>
        await _store.GetAsync(jobId, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");

    private void EnsureRecognizerMatches(JobRecord job)
    {
        if (job.Recognizer is null
            || !string.Equals(job.Recognizer.Id, _recognizer.Identity.Id, StringComparison.Ordinal)
            || !string.Equals(job.Recognizer.Version, _recognizer.Identity.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Job '{job.Id}' requires recognizer '{job.Recognizer?.Id}@{job.Recognizer?.Version}', but '{_recognizer.Identity.Id}@{_recognizer.Identity.Version}' is active.");
        }
    }

    private static InvalidDataException InvalidCheckpoint(Guid jobId, int inputIndex) =>
        new($"Page checkpoint {inputIndex} for job '{jobId}' is incomplete.");

    private static JobLeaseLostException LeaseLost(Guid jobId, Guid runId) =>
        new(jobId, runId, "The job run lease expired or is owned by another runner.");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class LeaseHeartbeat : IAsyncDisposable
    {
        private readonly IJobStore _store;
        private readonly Guid _jobId;
        private readonly Guid _runId;
        private readonly TimeProvider _timeProvider;
        private readonly TimeSpan _leaseDuration;
        private readonly TimeSpan _heartbeatInterval;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _executionCancellation;
        private readonly Task _loop;
        private Exception? _failure;

        public LeaseHeartbeat(
            IJobStore store,
            Guid jobId,
            Guid runId,
            TimeProvider timeProvider,
            TimeSpan leaseDuration,
            TimeSpan heartbeatInterval,
            CancellationTokenSource executionCancellation)
        {
            _store = store;
            _jobId = jobId;
            _runId = runId;
            _timeProvider = timeProvider;
            _leaseDuration = leaseDuration;
            _heartbeatInterval = heartbeatInterval;
            _executionCancellation = executionCancellation;
            _loop = RunAsync();
        }

        public bool LeaseLost => Volatile.Read(ref _failure) is not null;

        public JobLeaseLostException CreateLeaseLostException() =>
            new(
                _jobId,
                _runId,
                "The job run lease could not be renewed.",
                Volatile.Read(ref _failure));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }

            _stop.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(
                        _heartbeatInterval,
                        _timeProvider,
                        _stop.Token).ConfigureAwait(false);
                    var now = _timeProvider.GetUtcNow();
                    if (!await _store.RenewLeaseAsync(
                        _jobId,
                        _runId,
                        now,
                        now + _leaseDuration,
                        _stop.Token).ConfigureAwait(false))
                    {
                        LoseLease(new InvalidOperationException("The lease renewal was rejected."));
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LoseLease(exception);
            }
        }

        private void LoseLease(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _failure, exception, null) is not null)
            {
                return;
            }

            try
            {
                _executionCancellation.Cancel();
            }
            catch (Exception cancellationException)
            {
                Volatile.Write(
                    ref _failure,
                    new AggregateException(exception, cancellationException));
            }
        }
    }
}

public abstract record JobExecutionResult
{
    private JobExecutionResult() { }

    public sealed record Finished(Guid JobId, JobState State, PipelineResult Pipeline) : JobExecutionResult;

    public sealed record Paused(Guid JobId, int CompletedPages, int TotalPages) : JobExecutionResult;
}

public sealed class JobInputChangedException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class JobLeaseLostException(
    Guid jobId,
    Guid runId,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public Guid JobId { get; } = jobId;
    public Guid RunId { get; } = runId;
}
