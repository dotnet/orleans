using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableJobs;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT")]
public class DurableJobFeatureHandlerTests
{
    [Fact]
    public void Registry_RejectsDuplicateJobNames()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();

        registry.Register("feature", handler);

        Assert.Throws<InvalidOperationException>(() => registry.Register("feature", handler));
    }

    [Fact]
    public void RetryAt_CreatesDurableRescheduleResult()
    {
        var dueTime = DateTimeOffset.UtcNow.AddMinutes(1);

        var result = DurableJobRunResult.RetryAt(dueTime);

        Assert.True(result.IsRetryRequested);
        Assert.Equal(DurableJobRunStatus.RetryAt, result.Status);
        Assert.Equal(dueTime, result.RetryAtTime);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Registry_Register_ThrowsForNullEmptyOrWhitespaceJobName(string? jobName)
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();

        var expectedType = jobName is null ? typeof(ArgumentNullException) : typeof(ArgumentException);
        var ex = Record.Exception(() => registry.Register(jobName!, handler));

        Assert.NotNull(ex);
        Assert.IsType(expectedType, ex);
    }

    [Fact]
    public void Registry_Register_ThrowsArgumentNullException_ForNullHandler()
    {
        var registry = new DurableJobHandlerRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register("feature", null!));
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsTrueAndHandler_WhenRegistered()
    {
        var registry = new DurableJobHandlerRegistry();
        var handler = new TestHandler();
        registry.Register("feature", handler);

        var found = registry.TryGetHandler("feature", out var resolved);

        Assert.True(found);
        Assert.Same(handler, resolved);
    }

    [Fact]
    public void Registry_TryGetHandler_ReturnsFalseAndNull_WhenJobNameNotRegistered()
    {
        var registry = new DurableJobHandlerRegistry();
        registry.Register("feature", new TestHandler());

        var found = registry.TryGetHandler("some-other-job", out var resolved);

        Assert.False(found);
        Assert.Null(resolved);
    }

    [Fact]
    public void PollAfter_ThrowsArgumentOutOfRangeException_ForZeroDelay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.PollAfter(TimeSpan.Zero));
        Assert.Equal("delay", ex.ParamName);
    }

    [Fact]
    public void PollAfter_ThrowsArgumentOutOfRangeException_ForNegativeDelay()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DurableJobRunResult.PollAfter(TimeSpan.FromSeconds(-1)));
        Assert.Equal("delay", ex.ParamName);
    }

    [Fact]
    public void Failed_ThrowsArgumentNullException_ForNullException()
    {
        Assert.Throws<ArgumentNullException>(() => DurableJobRunResult.Failed(null!));
    }

    [Fact]
    public void Completed_ReturnsSameSingletonInstance_OnEveryAccess()
    {
        var first = DurableJobRunResult.Completed;
        var second = DurableJobRunResult.Completed;

        Assert.Same(first, second);
        Assert.Equal(DurableJobRunStatus.Completed, first.Status);
    }

    [Theory]
    [InlineData(DurableJobRunStatus.Completed)]
    [InlineData(DurableJobRunStatus.PollAfter)]
    [InlineData(DurableJobRunStatus.Failed)]
    [InlineData(DurableJobRunStatus.RetryAt)]
    public void StatusFlags_AreMutuallyExclusive_AcrossAllStatuses(DurableJobRunStatus status)
    {
        var result = status switch
        {
            DurableJobRunStatus.Completed => DurableJobRunResult.Completed,
            DurableJobRunStatus.PollAfter => DurableJobRunResult.PollAfter(TimeSpan.FromSeconds(30)),
            DurableJobRunStatus.Failed => DurableJobRunResult.Failed(new InvalidOperationException("boom")),
            DurableJobRunStatus.RetryAt => DurableJobRunResult.RetryAt(DateTimeOffset.UtcNow.AddMinutes(5)),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unexpected status."),
        };

        Assert.Equal(status, result.Status);

        var trueFlagCount = (result.IsFailed ? 1 : 0) + (result.IsPending ? 1 : 0) + (result.IsRetryRequested ? 1 : 0);

        switch (status)
        {
            case DurableJobRunStatus.Completed:
                Assert.Equal(0, trueFlagCount);
                Assert.False(result.IsFailed);
                Assert.False(result.IsPending);
                Assert.False(result.IsRetryRequested);
                break;
            case DurableJobRunStatus.PollAfter:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsPending);
                Assert.False(result.IsFailed);
                Assert.False(result.IsRetryRequested);
                break;
            case DurableJobRunStatus.Failed:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsFailed);
                Assert.False(result.IsPending);
                Assert.False(result.IsRetryRequested);
                break;
            case DurableJobRunStatus.RetryAt:
                Assert.Equal(1, trueFlagCount);
                Assert.True(result.IsRetryRequested);
                Assert.False(result.IsFailed);
                Assert.False(result.IsPending);
                break;
        }
    }

    private sealed class TestHandler : IDurableJobFeatureHandler
    {
        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
