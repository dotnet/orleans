using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;
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
    public void DefaultResult_RequiresDeduplicationBeforeRetry()
    {
        var result = default(ClusterServiceOperationResult<int>);

        Assert.Equal(ClusterServiceExecutionDisposition.OutcomeUnknown, result.Disposition);
        Assert.False(result.CanRetryWithoutDeduplication);
    }

    [Fact]
    public void GeneratedSerializer_MissingDispositionRequiresDeduplicationBeforeRetry()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        using var session = serializer.SessionPool.GetSession();
        var buffer = new ArrayBufferWriter<byte>();
        var writer = Writer.Create(buffer, session);
        writer.WriteFieldHeaderExpected(0, WireType.TagDelimited);
        session.CodecProvider.GetCodec<ClusterServiceViewId>().WriteField(ref writer, 1, typeof(ClusterServiceViewId), View);
        session.CodecProvider.GetCodec<ClusterServiceRetryReason>().WriteField(
            ref writer, 2, typeof(ClusterServiceRetryReason), ClusterServiceRetryReason.WrongView);
        writer.WriteEndObject();
        writer.Commit();

        var result = serializer.Deserialize<ClusterServiceOperationResult<int>>(buffer.WrittenSpan);

        Assert.Equal(View, result.ViewId);
        Assert.Equal(ClusterServiceRetryReason.WrongView, result.RetryReason);
        Assert.Equal(ClusterServiceExecutionDisposition.OutcomeUnknown, result.Disposition);
        Assert.False(result.CanRetryWithoutDeduplication);
    }

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

    [Fact]
    public void GeneratedSerializer_RoundTripsOperationResult()
    {
        using var serviceProvider = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var expectedResults = new[]
        {
            ClusterServiceOperationResult<int>.Executed(42, View),
            ClusterServiceOperationResult<int>.Rejected(
                View,
                ClusterServiceRetryReason.SafetyDelay,
                TimeSpan.FromMilliseconds(250)),
            ClusterServiceOperationResult<int>.Unknown(View),
            default,
        };

        foreach (var expected in expectedResults)
        {
            var actual = serializer.Deserialize<ClusterServiceOperationResult<int>>(
                serializer.SerializeToArray(expected));

            Assert.Equal(expected.Value, actual.Value);
            Assert.Equal(expected.ViewId, actual.ViewId);
            Assert.Equal(expected.Disposition, actual.Disposition);
            Assert.Equal(expected.RetryReason, actual.RetryReason);
            Assert.Equal(expected.RetryAfter, actual.RetryAfter);
            Assert.Equal(expected.CanRetryWithoutDeduplication, actual.CanRetryWithoutDeduplication);
        }
    }
}
