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

    private sealed class TestHandler : IDurableJobFeatureHandler
    {
        public ValueTask<DurableJobRunResult> ExecuteJobAsync(IJobRunContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(DurableJobRunResult.Completed);
    }
}
