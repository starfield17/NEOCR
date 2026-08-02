using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed record JobRecord(
    Guid Id,
    JobSpec Spec,
    JobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    RecognizerIdentity? Recognizer = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    Guid? RunId = null,
    DateTimeOffset? LeaseExpiresAt = null);

public sealed record JobRunLease(Guid JobId, Guid RunId, DateTimeOffset ExpiresAt);

public enum PageCheckpointState
{
    Pending = 1,
    Succeeded = 2,
    Declined = 3,
}

public sealed record PageCheckpoint(
    Guid JobId,
    int InputIndex,
    PageArtifact Page,
    PageCheckpointState State,
    RecognitionResult? Result = null,
    string? DeclineReasonCode = null,
    string? DeclineMessage = null,
    DateTimeOffset? UpdatedAt = null);

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<JobRecord> EnqueueAsync(
        JobSpec spec,
        RecognizerIdentity recognizer,
        CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRecord>> ListAsync(
        JobState state,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>> ReconcileAbandonedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
    Task<JobRunLease?> TryClaimAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
    Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PageCheckpoint>> GetPagesAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task InitializePagesAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        IReadOnlyList<PageArtifact> pages,
        CancellationToken cancellationToken = default);
    Task<bool> SavePageSucceededAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        RecognitionResult result,
        CancellationToken cancellationToken = default);
    Task<bool> SavePageDeclinedAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        string reasonCode,
        string message,
        CancellationToken cancellationToken = default);
    Task<bool> TransitionAsync(
        Guid id,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
    Task<bool> TransitionOwnedAsync(
        Guid id,
        Guid runId,
        DateTimeOffset now,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
}

public sealed class JobRecoveryService(IJobStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<IReadOnlyList<Guid>> ReconcileAbandonedAsync(
        CancellationToken cancellationToken = default) =>
        store.ReconcileAbandonedAsync(_timeProvider.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<JobRecord>> ListPausedAsync(
        CancellationToken cancellationToken = default) =>
        store.ListAsync(JobState.Paused, cancellationToken);

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await store.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        return job?.State switch
        {
            JobState.Queued => await store.TransitionAsync(
                jobId, JobState.Queued, JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            JobState.Paused => await store.TransitionAsync(
                jobId, JobState.Paused, JobState.Cancelled,
                cancellationToken: cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }
}

public static class JobStateMachine
{
    public static bool CanTransition(JobState source, JobState target) => (source, target) switch
    {
        (JobState.Queued, JobState.Running or JobState.Cancelled) => true,
        (JobState.Running, JobState.Pausing
            or JobState.Completed
            or JobState.CompletedWithErrors
            or JobState.Failed
            or JobState.Cancelled) => true,
        (JobState.Pausing, JobState.Paused
            or JobState.Completed
            or JobState.CompletedWithErrors
            or JobState.Failed
            or JobState.Cancelled) => true,
        (JobState.Paused, JobState.Queued or JobState.Cancelled) => true,
        _ => false,
    };

    public static bool IsTerminal(JobState state) => state is JobState.Completed
        or JobState.CompletedWithErrors
        or JobState.Failed
        or JobState.Cancelled;

    public static void EnsureTransition(JobState source, JobState target)
    {
        if (!CanTransition(source, target))
        {
            throw new InvalidOperationException($"Invalid job transition: {source} -> {target}.");
        }
    }
}
