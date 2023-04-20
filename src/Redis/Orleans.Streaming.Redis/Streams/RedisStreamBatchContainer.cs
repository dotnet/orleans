using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis;

[GenerateSerializer]
[Serializable]
internal sealed class RedisStreamBatchContainer : IBatchContainer
{
    [Id(0)]
    private readonly StreamId _streamId;
    [Id(1)]
    private readonly List<object> _events;
    [Id(2)]
    private readonly Dictionary<string, object> _requestContext;
    [Id(3)]
    private EventSequenceTokenV2 _sequenceToken;

    private RedisStreamBatchContainer(
        StreamId streamId,
        List<object> events,
        Dictionary<string, object> requestContext)
    {
        _streamId = streamId;
        _events = events;
        _requestContext = requestContext;
    }

    [NonSerialized]
    public StreamEntry Entry;

    public StreamId StreamId => _streamId;

    public StreamSequenceToken SequenceToken => _sequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => _events.OfType<T>().Select((x, i) => Tuple.Create<T, StreamSequenceToken>(x, _sequenceToken.CreateSequenceTokenForEvent(i)));

    public bool ImportRequestContext()
    {
        if (_requestContext is not null)
        {
            RequestContextExtensions.Import(_requestContext);
            return true;
        }
        return false;
    }

    public static RedisValue ToRedisValue<T>(
        Serializer<RedisStreamBatchContainer> serializer,
        StreamId streamId,
        IEnumerable<T> events,
        Dictionary<string, object> requestContext)
    {
        var message = new RedisStreamBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var payload = serializer.SerializeToArray(message);
        return payload;
    }

    public static RedisStreamBatchContainer FromStreamEntry(
        Serializer<RedisStreamBatchContainer> serializer,
        StreamEntry entry,
        long seqNumber)
    {
        var message = serializer.Deserialize((byte[])entry.Values[0].Value);
        message._sequenceToken = new EventSequenceTokenV2(seqNumber);
        message.Entry = entry;
        return message;
    }
}
