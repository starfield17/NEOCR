using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core.Tests;

internal sealed class MemoryJobStore : IJobStore
{
    public Dictionary<Guid, JobRecord> Jobs { get; } = [];
    public Dictionary<Guid, List<PageCheckpoint>> Pages { get; } = [];
    public Func<Guid, Task>? BeforeGetPagesAsync { get; set; }
    public bool RejectLeaseRenewals { get; set; }

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

    public Task<IReadOnlyList<JobRecord>> ListAsync(
        JobState state,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<JobRecord>>(Jobs.Values
            .Where(job => job.State == state)
            .OrderByDescending(job => job.UpdatedAt)
            .ToArray());

    public Task<IReadOnlyList<Guid>> ReconcileAbandonedAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var recovered = Jobs.Values
            .Where(job => job.State is JobState.Running or JobState.Pausing
                && (job.LeaseExpiresAt is null || job.LeaseExpiresAt <= now))
            .Select(job => job.Id)
            .ToArray();
        foreach (var id in recovered)
        {
            Jobs[id] = Jobs[id] with
            {
                State = JobState.Paused,
                UpdatedAt = now,
                RunId = null,
                LeaseExpiresAt = null,
            };
        }

        return Task.FromResult<IReadOnlyList<Guid>>(recovered);
    }

    public Task<JobRunLease?> TryClaimAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (!Jobs.TryGetValue(jobId, out var job) || job.State != JobState.Queued)
        {
            return Task.FromResult<JobRunLease?>(null);
        }

        Jobs[jobId] = job with
        {
            State = JobState.Running,
            UpdatedAt = now,
            ErrorCode = null,
            ErrorMessage = null,
            RunId = runId,
            LeaseExpiresAt = leaseExpiresAt,
        };
        return Task.FromResult<JobRunLease?>(new(jobId, runId, leaseExpiresAt));
    }

    public Task<bool> RenewLeaseAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        if (RejectLeaseRenewals || !HasLiveLease(jobId, runId, now, out var job))
        {
            return Task.FromResult(false);
        }

        Jobs[jobId] = job with { UpdatedAt = now, LeaseExpiresAt = leaseExpiresAt };
        return Task.FromResult(true);
    }

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
        Guid runId,
        DateTimeOffset now,
        IReadOnlyList<PageArtifact> pages,
        CancellationToken cancellationToken = default)
    {
        if (!HasLiveLease(jobId, runId, now, out _))
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
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        RecognitionResult result,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(
            jobId, runId, now, inputIndex, expectedStableId,
            PageCheckpointState.Succeeded, result, null, null);

    public Task<bool> SavePageDeclinedAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        string reasonCode,
        string message,
        CancellationToken cancellationToken = default) =>
        SavePageAsync(
            jobId, runId, now, inputIndex, expectedStableId,
            PageCheckpointState.Declined, null, reasonCode, message);

    public Task<bool> TransitionAsync(
        Guid id,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (expected is JobState.Running or JobState.Pausing
            || target is JobState.Running or JobState.Pausing)
        {
            throw new InvalidOperationException("Running job transitions require a run lease.");
        }

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
            RunId = null,
            LeaseExpiresAt = null,
        };
        if (JobStateMachine.IsTerminal(target))
        {
            Pages.Remove(id);
        }

        return Task.FromResult(true);
    }

    public Task<bool> TransitionOwnedAsync(
        Guid id,
        Guid runId,
        DateTimeOffset now,
        JobState expected,
        JobState target,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasLiveLease(id, runId, now, out var record) || record.State != expected)
        {
            return Task.FromResult(false);
        }

        var clearLease = target == JobState.Paused || JobStateMachine.IsTerminal(target);
        Jobs[id] = record with
        {
            State = target,
            UpdatedAt = now,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            RunId = clearLease ? null : runId,
            LeaseExpiresAt = clearLease ? null : record.LeaseExpiresAt,
        };
        if (JobStateMachine.IsTerminal(target))
        {
            Pages.Remove(id);
        }

        return Task.FromResult(true);
    }

    private Task<bool> SavePageAsync(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        int inputIndex,
        string expectedStableId,
        PageCheckpointState state,
        RecognitionResult? result,
        string? declineReasonCode,
        string? declineMessage)
    {
        if (!HasLiveLease(jobId, runId, now, out _)
            || !Pages.TryGetValue(jobId, out var pages)
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

    private bool HasLiveLease(
        Guid jobId,
        Guid runId,
        DateTimeOffset now,
        out JobRecord job)
    {
        if (Jobs.TryGetValue(jobId, out job!)
            && job.State is JobState.Running or JobState.Pausing
            && job.RunId == runId
            && job.LeaseExpiresAt > now)
        {
            return true;
        }

        job = null!;
        return false;
    }
}
