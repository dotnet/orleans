using System;
using System.Collections.Generic;
using System.Globalization;
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
    private RedisStreamSequenceToken _sequenceToken = new();

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
        string fieldName)
    {
        var payloadEntry = entry.Values.FirstOrDefault(value => string.Equals(value.Name.ToString(), fieldName, StringComparison.Ordinal));
        if (payloadEntry.Name.IsNull)
        {
            throw new RedisStreamingException($"Redis stream entry '{entry.Id}' does not contain the '{fieldName}' payload field.");
        }

        var payload = (byte[]?)payloadEntry.Value ?? throw new RedisStreamingException($"Redis stream entry '{entry.Id}' contains an empty '{fieldName}' payload field.");
        var message = serializer.Deserialize(payload);
        var (sequenceNumber, redisSequenceNumber) = ParseEntryId(entry.Id);
        message._sequenceToken = new RedisStreamSequenceToken(entry.Id.ToString(), sequenceNumber, redisSequenceNumber, 0);
        message.Entry = entry;
        return message;
    }

    internal static (long SequenceNumber, long RedisSequenceNumber) ParseEntryId(RedisValue entryId)
    {
        var value = entryId.ToString();
        var separatorIndex = value.IndexOf('-', StringComparison.Ordinal);
        if (separatorIndex < 0)
        {
            throw new FormatException($"Invalid Redis stream entry identifier '{value}'.");
        }

        return (
            long.Parse(value[..separatorIndex], CultureInfo.InvariantCulture),
            long.Parse(value[(separatorIndex + 1)..], CultureInfo.InvariantCulture));
    }
}
