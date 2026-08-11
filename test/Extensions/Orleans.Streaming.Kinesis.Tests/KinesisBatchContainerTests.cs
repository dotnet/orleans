using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.Kinesis;
using TestExtensions;
using Xunit;
using KinesisRecord = Amazon.Kinesis.Model.Record;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
[Collection(TestEnvironmentFixture.DefaultCollection)]
public sealed class KinesisBatchContainerTests
{
    private readonly Serializer<KinesisBatchContainer.Body> serializer;

    public KinesisBatchContainerTests(TestEnvironmentFixture fixture)
    {
        serializer = fixture.Services.GetRequiredService<Serializer<KinesisBatchContainer.Body>>();
    }

    [Fact]
    public void GetEventsFiltersByRequestedType()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var payload = KinesisBatchContainer.ToKinesisPayload(
            serializer,
            streamId,
            new object[] { 1, "two", 3 },
            requestContext: null);
        var record = new KinesisRecord
        {
            Data = new MemoryStream(payload),
            SequenceNumber = "1",
        };
        var batch = KinesisBatchContainer.FromKinesisRecord(serializer, record, sequenceId: 0);

        Assert.Equal([1, 3], batch.GetEvents<int>().Select(item => item.Item1));
        Assert.Equal(["two"], batch.GetEvents<string>().Select(item => item.Item1));
    }
}
