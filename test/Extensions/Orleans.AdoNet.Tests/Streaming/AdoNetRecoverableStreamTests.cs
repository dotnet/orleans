using Orleans.Streaming.AdoNet;

namespace Tester.AdoNet.Streaming;

[TestCategory("BVT"), TestCategory("AdoNet"), TestCategory("Streaming")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Streaming")]
public class AdoNetRecoverableStreamTests
{
    [Fact]
    public void ResolveCheckpointUpdate_ReturnsAuthoritativeStateForExpectedVersionConflict()
    {
        var update = new AdoNetStreamCheckpointUpdate(
            "service",
            "provider",
            "queue",
            OwnerEpoch: 7,
            Checkpoint: 42,
            Updated: false);

        var result = AdoNetRecoverableStream.ResolveCheckpointUpdate("service/provider/queue", 7, update);

        Assert.Equal("42", result.Checkpoint);
        Assert.Equal("7", result.Version);
    }

    [Fact]
    public void ResolveCheckpointUpdate_ThrowsWhenPartitionOwnershipIsLost()
    {
        var update = new AdoNetStreamCheckpointUpdate(
            "service",
            "provider",
            "queue",
            OwnerEpoch: 8,
            Checkpoint: 42,
            Updated: false);

        var exception = Assert.Throws<InvalidOperationException>(
            () => AdoNetRecoverableStream.ResolveCheckpointUpdate("service/provider/queue", 7, update));

        Assert.Contains("ownership was lost", exception.Message);
        Assert.Contains("service/provider/queue", exception.Message);
        Assert.Contains("epoch 7", exception.Message);
    }
}
