using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.Kinesis;
using Orleans.Streams;
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

    [Theory]
    [InlineData("7", "7")]
    [InlineData("7", "0007")]
    [InlineData("0007", "7")]
    [InlineData("0", "000")]
    [InlineData("000", "0")]
    [InlineData("170141183460469231731687303715884105727", "000170141183460469231731687303715884105727")]
    [InlineData("000170141183460469231731687303715884105727", "170141183460469231731687303715884105727")]
    public void BatchFilters_RespectNumericShardSequenceIdentity(string recordOffset, string recoveredOffset)
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var payload = KinesisBatchContainer.ToKinesisPayload(
            serializer, streamId, new object[] { "first", 2, "third", "fourth" }, requestContext: null);
        var batch = KinesisBatchContainer.FromCachedRecord(serializer, streamId, payload, recordOffset, sequenceId: 7);
        var filter = (IQueueCacheBatchContainerFilter)batch;
        var token = new KinesisSequenceToken(recoveredOffset, sequenceNumber: 999, eventIndex: 2);

        var inclusive = Assert.IsType<KinesisBatchContainer>(filter.FilterFrom(token));
        Assert.Equal(["third", "fourth"], inclusive.GetEvents<string>().Select(item => item.Item1));
        Assert.Equal([2, 3], inclusive.GetEvents<string>().Select(item => item.Item2.EventIndex));
        Assert.Empty(inclusive.GetEvents<int>());
        Assert.Equal(streamId, inclusive.StreamId);
        Assert.Equal(recordOffset, inclusive.Token.ShardSequence);
        Assert.Equal(7, inclusive.Token.SequenceNumber);

        var exclusive = Assert.IsType<KinesisBatchContainer>(filter.FilterAfter(token));
        Assert.Equal(["fourth"], exclusive.GetEvents<string>().Select(item => item.Item1));
        Assert.Equal([3], exclusive.GetEvents<string>().Select(item => item.Item2.EventIndex));
        Assert.Empty(exclusive.GetEvents<int>());
        Assert.Equal(streamId, exclusive.StreamId);
        Assert.Equal(recordOffset, exclusive.Token.ShardSequence);
        Assert.Equal(7, exclusive.Token.SequenceNumber);
        Assert.Null(filter.FilterAfter(new KinesisSequenceToken(recoveredOffset, sequenceNumber: 999, eventIndex: 3)));
        Assert.Equal(["first", "third", "fourth"], batch.GetEvents<string>().Select(item => item.Item1));
    }

    [Theory]
    [InlineData("7", "8")]
    [InlineData("0008", "7")]
    [InlineData("170141183460469231731687303715884105727", "170141183460469231731687303715884105728")]
    public void BatchFilters_PreserveBatchesFromDifferentRecords(string recordOffset, string recoveredOffset)
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var payload = KinesisBatchContainer.ToKinesisPayload(serializer, streamId, new[] { "first", "second" }, requestContext: null);
        var batch = KinesisBatchContainer.FromCachedRecord(serializer, streamId, payload, recordOffset, sequenceId: 7);
        var filter = (IQueueCacheBatchContainerFilter)batch;
        var token = new KinesisSequenceToken(recoveredOffset, sequenceNumber: 7, eventIndex: 1);

        Assert.Same(batch, filter.FilterFrom(token));
        Assert.Same(batch, filter.FilterAfter(token));
    }

    [Fact]
    public void RecoverableDataAdapter_PreservesRawPayloadAndExternalOffsetOrdering()
    {
        var streamId = StreamId.Create("test", Guid.NewGuid());
        var payload = KinesisBatchContainer.ToKinesisPayload(
            serializer,
            streamId,
            new[] { "event" },
            requestContext: null);
        var record = new KinesisRecord
        {
            Data = new MemoryStream(payload),
            SequenceNumber = "123456789012345678901234567890",
        };
        var queueMessage = new KinesisCacheRecord(record, sequenceNumber: 7);
        var adapter = new KinesisRecoverableStreamDataAdapter(serializer);

        var position = adapter.GetStreamPosition(queueMessage);
        var rawPayload = queueMessage.RawPayload;
        record.Data = null!;
        var cached = adapter.FromQueueMessage(
            position,
            queueMessage,
            DateTime.UtcNow,
            size => new byte[size]);

        Assert.Equal(streamId, cached.StreamId);
        Assert.Same(rawPayload, queueMessage.RawPayload);
        Assert.Equal(record.SequenceNumber, adapter.GetOffset(ref cached));
        Assert.True(adapter.Compare(
            ref cached,
            new KinesisSequenceToken("123456789012345678901234567889", 1000, 0)) > 0);
        Assert.Equal(0, adapter.Compare(
            ref cached,
            new KinesisSequenceToken("000123456789012345678901234567890", 1000, 0)));

        var batch = Assert.IsType<KinesisBatchContainer>(adapter.GetBatchContainer(ref cached));
        Assert.Equal(streamId, batch.StreamId);
        Assert.Equal(["event"], batch.GetEvents<string>().Select(item => item.Item1));
        Assert.Equal(record.SequenceNumber, ((KinesisSequenceToken)batch.SequenceToken).ShardSequence);
    }
}
