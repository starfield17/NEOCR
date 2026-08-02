using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core.Tests;

internal sealed class MemoryJobStore : IJobStore
{
    public Dictionary<Guid, JobRecord> Jobs { get; } = [];
    public Dictionary<Guid, List<PageCheckpoint>> Pages { get; } = [];
    public Func<Guid, Task>? BeforeGetPagesAsync { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<JobRecord> EnqueueAsync(
        JobSpec spec,
        RecognizerIdentity recognizer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var record = new JobRecord(Guid.NewGuid(), spec, JobState.Queued, now, now, recognizer);
        Jobs.Add(record.Id, record);
        return Task.FromResult(record);
    }

    public Task<JobRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Jobs.GetValueOrDefault(id));

    public async Task<IReadOnlyList<PageCheckpoint>> GetPagesAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (BeforeGetPagesAsync is not null)
        {
            await BeforeGetPagesAsync(jobId);
        }

        return Pages.TryGetValue(jobId, out var pages) ? pages.ToArray() : [];
    }

    public Task InitializePagesAsync(
        Guid jobId,
        IReadOnlyList<PageArtifact> pages,
        CancellationToken cancellationToken = default)
    {
        if (!Jobs.TryGetValue(jobId, out var job)
            || job.State is not (JobState.Running or JobState.Pausing))
        {
            throw new InvalidOperationException($"Pages for job '{jobId}' cannot be initialized.");
        }

        Pages.Add(jobId, pages.Select((page, index) => new PageCheckpoint(
            jobId,
            index,
            page,
            PageCheckpointState.Pending)).ToList());
        return Task.CompletedTask;
    }

    public Task<bool> SavePageSucceededAsync(
        Guid jobId,
        int inputIndex,
        string expectedStableId,
        RecognitionResult result,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(jobId, inputIndex, expectedStableId, PageCheckpointState.Succeeded, result, null, null);

    public Task<bool> SavePageDeclinedAsync(
        Guid jobId,
        int inputIndex,
        string expectedStableId,
        string reasonCode,
        string message,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(jobId, inputIndex, expectedStableId, PageCheckpointState.Declined, null, reasonCode, message);

    public Task<bool> TransitionAsync(
        Guid id,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!Jobs.TryGetValue(id, out var record) || record.State != expected)
        {
            return Task.FromResult(false);
        }

        Jobs[id] = record with
        {
            State = target,
            UpdatedAt = DateTimeOffset.UtcNow,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
        if (JobStateMachine.IsTerminal(target))
        {
            Pages.Remove(id);
        }

        return Task.FromResult(true);
    }

    private Task<bool> SavePageAsync(
        Guid jobId,
        int inputIndex,
        string expectedStableId,
        PageCheckpointState state,
        RecognitionResult? result,
        string? declineReasonCode,
        string? declineMessage)
    {
        if (!Pages.TryGetValue(jobId, out var pages)
            || inputIndex < 0
            || inputIndex >= pages.Count
            || pages[inputIndex].State != PageCheckpointState.Pending
            || !string.Equals(pages[inputIndex].Page.StableId, expectedStableId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        pages[inputIndex] = pages[inputIndex] with
        {
            State = state,
            Result = result,
            DeclineReasonCode = declineReasonCode,
            DeclineMessage = declineMessage,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(true);
    }
}
