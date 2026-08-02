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
    string? ErrorMessage = null);

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
    Task<IReadOnlyList<PageCheckpoint>> GetPagesAsync(
        Guid jobId,
        CancellationToken cancellationToken = default);
    Task InitializePagesAsync(
        Guid jobId,
        IReadOnlyList<PageArtifact> pages,
        CancellationToken cancellationToken = default);
    Task<bool> SavePageSucceededAsync(
        Guid jobId,
        int inputIndex,
        string expectedStableId,
        RecognitionResult result,
        CancellationToken cancellationToken = default);
    Task<bool> SavePageDeclinedAsync(
        Guid jobId,
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
