using System.Reflection;
using Orleans.DurableJobs;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobRunResultTests
{
    [Fact]
    public void StatusValuesAndSerializedMemberIdsRemainCompatible()
    {
        Assert.Equal(0, (int)DurableJobRunStatus.Completed);
        Assert.Equal(1, (int)DurableJobRunStatus.Running);
        Assert.Equal(2, (int)DurableJobRunStatus.Failed);
        Assert.Equal(3, (int)DurableJobRunStatus.RescheduleRequested);

        Assert.Equal(0u, GetId(nameof(DurableJobRunResult.Status)));
        Assert.Equal(1u, GetId(nameof(DurableJobRunResult.PollAfterDelay)));
        Assert.Equal(2u, GetId(nameof(DurableJobRunResult.Exception)));
        Assert.Equal(3u, GetId(nameof(DurableJobRunResult.RescheduleTime)));
    }

    [Fact]
    public void Completed_HasOnlyCompletedStatus()
    {
        var result = DurableJobRunResult.Completed;

        Assert.Equal(DurableJobRunStatus.Completed, result.Status);
        Assert.False(result.IsRunning);
        Assert.False(result.IsFailed);
        Assert.False(result.IsRescheduleRequested);
        Assert.Null(result.PollAfterDelay);
        Assert.Null(result.Exception);
        Assert.Null(result.RescheduleTime);
    }

    [Fact]
    public void Running_RequiresPositiveDelayAndExposesPollDelay()
    {
        var delay = TimeSpan.FromSeconds(5);

        var result = DurableJobRunResult.Running(delay);

        Assert.Equal(DurableJobRunStatus.Running, result.Status);
        Assert.True(result.IsRunning);
        Assert.Equal(delay, result.PollAfterDelay);
        Assert.False(result.IsFailed);
        Assert.False(result.IsRescheduleRequested);
        Assert.Null(result.Exception);
        Assert.Null(result.RescheduleTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.Running(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.Running(TimeSpan.FromTicks(-1)));
    }

    [Fact]
    public void Failed_RequiresExceptionAndExposesFailure()
    {
        var exception = new InvalidOperationException("failed");

        var result = DurableJobRunResult.Failed(exception);

        Assert.Equal(DurableJobRunStatus.Failed, result.Status);
        Assert.True(result.IsFailed);
        Assert.Same(exception, result.Exception);
        Assert.False(result.IsRunning);
        Assert.False(result.IsRescheduleRequested);
        Assert.Null(result.PollAfterDelay);
        Assert.Null(result.RescheduleTime);
        Assert.Throws<ArgumentNullException>(() => DurableJobRunResult.Failed(null!));
    }

    [Fact]
    public void RescheduleAt_CreatesSuccessfulDurableRescheduleResult()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);

        var result = DurableJobRunResult.RescheduleAt(dueTime);

        Assert.Equal(DurableJobRunStatus.RescheduleRequested, result.Status);
        Assert.True(result.IsRescheduleRequested);
        Assert.Equal(dueTime, result.RescheduleTime);
        Assert.False(result.IsFailed);
        Assert.False(result.IsRunning);
        Assert.Null(result.PollAfterDelay);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void IsRescheduleRequested_RequiresRescheduleTime()
    {
        var result = CreateResult(DurableJobRunStatus.RescheduleRequested);

        Assert.False(result.IsRescheduleRequested);
        Assert.Null(result.RescheduleTime);
    }

    private static uint GetId(string propertyName) =>
        Assert.Single(
            typeof(DurableJobRunResult)
                .GetProperty(propertyName)!
                .GetCustomAttributes(typeof(IdAttribute), inherit: false)
                .Cast<IdAttribute>()).Id;

    private static DurableJobRunResult CreateResult(DurableJobRunStatus status) =>
        (DurableJobRunResult)Activator.CreateInstance(
            typeof(DurableJobRunResult),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [status, null, null, null],
            culture: null)!;
}
