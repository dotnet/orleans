using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceOperationResultTests
{
    private static readonly ClusterServiceViewId View = new(new MembershipVersion(3), 1, "config");

    [Fact]
    public void RejectedBeforeExecution_IsTheOnlyDispositionSafeForAutomaticRetry()
    {
        var retryAfter = TimeSpan.FromMilliseconds(250);
        var rejected = ClusterServiceOperationResult<int>.Rejected(
            View,
            ClusterServiceRetryReason.SafetyDelay,
            retryAfter);
        var executed = ClusterServiceOperationResult<int>.Executed(42, View);
        var unknown = ClusterServiceOperationResult<int>.Unknown(View);

        Assert.True(rejected.CanRetryWithoutDeduplication);
        Assert.Equal(0, rejected.Value);
        Assert.Equal(View, rejected.ViewId);
        Assert.Equal(ClusterServiceExecutionDisposition.RejectedBeforeExecution, rejected.Disposition);
        Assert.Equal(ClusterServiceRetryReason.SafetyDelay, rejected.RetryReason);
        Assert.Equal(retryAfter, rejected.RetryAfter);

        Assert.False(executed.CanRetryWithoutDeduplication);
        Assert.Equal(42, executed.Value);
        Assert.Equal(View, executed.ViewId);
        Assert.Equal(ClusterServiceExecutionDisposition.Executed, executed.Disposition);
        Assert.Equal(ClusterServiceRetryReason.None, executed.RetryReason);
        Assert.Equal(TimeSpan.Zero, executed.RetryAfter);

        Assert.False(unknown.CanRetryWithoutDeduplication);
        Assert.Equal(0, unknown.Value);
        Assert.Equal(View, unknown.ViewId);
        Assert.Equal(ClusterServiceExecutionDisposition.OutcomeUnknown, unknown.Disposition);
        Assert.Equal(ClusterServiceRetryReason.MemberUnavailable, unknown.RetryReason);
        Assert.Equal(TimeSpan.Zero, unknown.RetryAfter);
    }

    [Fact]
    public void Rejected_RequiresReasonAndNonNegativeDelay()
    {
        var missingReason = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClusterServiceOperationResult<int>.Rejected(View, ClusterServiceRetryReason.None));
        var negativeDelay = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClusterServiceOperationResult<int>.Rejected(
                View,
                ClusterServiceRetryReason.SafetyDelay,
                TimeSpan.FromTicks(-1)));
        var zeroDelay = ClusterServiceOperationResult<int>.Rejected(
            View,
            ClusterServiceRetryReason.PartitionNotReady,
            TimeSpan.Zero);

        Assert.Equal("reason", missingReason.ParamName);
        Assert.Equal("retryAfter", negativeDelay.ParamName);
        Assert.Equal(TimeSpan.Zero, zeroDelay.RetryAfter);
        Assert.Equal(ClusterServiceRetryReason.PartitionNotReady, zeroDelay.RetryReason);
        Assert.True(zeroDelay.CanRetryWithoutDeduplication);
    }
}
