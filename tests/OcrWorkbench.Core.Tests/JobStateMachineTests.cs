using OcrWorkbench.Contracts;

namespace OcrWorkbench.Core.Tests;

public sealed class JobStateMachineTests
{
    [Theory]
    [InlineData(JobState.Queued, JobState.Running)]
    [InlineData(JobState.Running, JobState.Pausing)]
    [InlineData(JobState.Pausing, JobState.Paused)]
    [InlineData(JobState.Paused, JobState.Queued)]
    [InlineData(JobState.Running, JobState.CompletedWithErrors)]
    public void AcceptsSupportedTransitions(JobState source, JobState target)
    {
        Assert.True(JobStateMachine.CanTransition(source, target));
    }

    [Theory]
    [InlineData(JobState.Completed, JobState.Running)]
    [InlineData(JobState.Queued, JobState.Completed)]
    [InlineData(JobState.Paused, JobState.Completed)]
    public void RejectsUnsupportedTransitions(JobState source, JobState target)
    {
        Assert.False(JobStateMachine.CanTransition(source, target));
        Assert.Throws<InvalidOperationException>(() => JobStateMachine.EnsureTransition(source, target));
    }
}

