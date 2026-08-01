using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core;

public sealed record JobRecord(
    Guid Id,
    JobSpec Spec,
    JobState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<JobRecord> EnqueueAsync(JobSpec spec, CancellationToken cancellationToken = default);
    Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default);
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
        (JobState.Pausing, JobState.Paused or JobState.Failed or JobState.Cancelled) => true,
        (JobState.Paused, JobState.Queued or JobState.Cancelled) => true,
        _ => false,
    };

    public static void EnsureTransition(JobState source, JobState target)
    {
        if (!CanTransition(source, target))
        {
            throw new InvalidOperationException($"Invalid job transition: {source} -> {target}.");
        }
    }
}

