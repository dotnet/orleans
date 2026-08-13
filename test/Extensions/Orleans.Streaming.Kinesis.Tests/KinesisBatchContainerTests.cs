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

        Assert.False(batch.ImportRequestContext());
    }

    [Fact]
    public void GetEventsAssignsDistinctEventIndexPerEventWithinSameRecord()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var payload = KinesisBatchContainer.ToKinesisPayload(
            serializer,
            streamId,
            new object[] { "first", "second", "third" },
            requestContext: null);
        var record = new KinesisRecord
        {
            Data = new MemoryStream(payload),
            SequenceNumber = "999999999999999999999",
        };
        var batch = KinesisBatchContainer.FromKinesisRecord(serializer, record, sequenceId: 0);

        var tokens = batch.GetEvents<string>()
            .Select(item => (Value: item.Item1, Token: (KinesisSequenceToken)item.Item2))
            .ToArray();

        Assert.Equal(["first", "second", "third"], tokens.Select(t => t.Value));
        Assert.Equal([0, 1, 2], tokens.Select(t => t.Token.EventIndex));

        Assert.True(tokens[0].Token.CompareTo(tokens[1].Token) < 0);
        Assert.True(tokens[1].Token.CompareTo(tokens[2].Token) < 0);
        Assert.All(tokens, t => Assert.Equal(record.SequenceNumber, t.Token.ShardSequence));
    }

    [Fact]
    public void OldPayloadShapeWithRequestContextDecodesUnchanged()
    {
        const string legacyPayload = "ICAABQkDHUgFGWxlZ2FjeS1ldmVudOAhAQNBH2xlZ2FjeS10cmFjZS1pZEgFEXRyYWNlLTQy4CFAYWxlZ2FjeS1uYW1lc3BhY2UxMTExMTExMTIyMjIzMzMzNDQ0NDU1NTU1NTU1NTU1NQEhYSSIghng4A==";
        var streamId = StreamId.Create("legacy-namespace", Guid.Parse("11111111-2222-3333-4444-555555555555"));
        var requestContext = new Dictionary<string, object> { ["legacy-trace-id"] = "trace-42" };
        var payload = KinesisBatchContainer.ToKinesisPayload(
            serializer,
            streamId,
            new object[] { 7, "legacy-event" },
            requestContext);
        Assert.Equal(legacyPayload, Convert.ToBase64String(payload));

        var record = new KinesisRecord
        {
            Data = new MemoryStream(Convert.FromBase64String(legacyPayload)),
            SequenceNumber = "123456789012345678901234567890",
        };

        var batch = KinesisBatchContainer.FromKinesisRecord(serializer, record, sequenceId: 0);

        Assert.Equal(streamId, batch.StreamId);
        Assert.Equal([7], batch.GetEvents<int>().Select(item => item.Item1));
        Assert.Equal(["legacy-event"], batch.GetEvents<string>().Select(item => item.Item1));

        Assert.True(batch.ImportRequestContext());
        try
        {
            Assert.Equal("trace-42", RequestContext.Get("legacy-trace-id"));
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    [Fact]
    public void CompareToOrdersByDurableShardSequenceNotReceiverLocalOrdinal()
    {
        var readFirstButNewer = KinesisBatchContainer.FromKinesisRecord(
            serializer,
            new KinesisRecord { Data = new MemoryStream(), SequenceNumber = "200000000000000000000000000000" },
            sequenceId: 0);

        var readSecondButOlder = KinesisBatchContainer.FromKinesisRecord(
            serializer,
            new KinesisRecord { Data = new MemoryStream(), SequenceNumber = "1" },
            sequenceId: 1);

        Assert.True(readFirstButNewer.CompareTo(readSecondButOlder) > 0);
        Assert.True(readSecondButOlder.CompareTo(readFirstButNewer) < 0);
    }
}
