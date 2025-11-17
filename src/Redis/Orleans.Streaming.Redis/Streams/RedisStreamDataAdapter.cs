using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

[SerializationCallbacks(typeof(OnDeserializedCallbacks))]
public class RedisStreamDataAdapter(Serializer serializer) : IQueueDataAdapter<StreamEntry, IBatchContainer>, IOnDeserialized
{
    private Serializer<RedisStreamBatchContainer> serializer = serializer.GetSerializer<RedisStreamBatchContainer>();

    public IBatchContainer FromQueueMessage(StreamEntry queueMessage, long sequenceId)
    {
        var dataEntry = queueMessage.Values.FirstOrDefault(v => v.Name == "data");
        if (dataEntry.Equals(default))
        {
            throw new ArgumentException("Stream entry does not contain 'data' field.", nameof(queueMessage));
        }
        var base64String = (string)dataEntry.Value;
        var rawBytes = Convert.FromBase64String(base64String);
        var redisStreamBatchContainer = serializer.Deserialize(rawBytes);        
        redisStreamBatchContainer.RealSequenceToken = GetSequenceTokenFromStreamEntryId(queueMessage.Id);
                
        return redisStreamBatchContainer;
    }

    internal static EventSequenceTokenV2 GetSequenceTokenFromStreamEntryId(RedisValue streamEntryId)
    {
        var redisValueId = streamEntryId.ToString();

        var splitIndex = redisValueId.IndexOf('-');
        if (splitIndex < 0)
        {
            throw new ArgumentException(message: $"Invalid {nameof(streamEntryId)}", paramName: nameof(streamEntryId));
        }

        var sequenceNumber = long.Parse(redisValueId.AsSpan(0, splitIndex));
        return new EventSequenceTokenV2(sequenceNumber);
    }

    public void OnDeserialized(DeserializationContext context)
    {
        serializer = context.ServiceProvider.GetRequiredService<Serializer<RedisStreamBatchContainer>>();
    }

    public StreamEntry ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        var redisStreamBatchContainer = new RedisStreamBatchContainer(streamId, [.. events.Cast<object>()], requestContext);
        var rawBytes = serializer.SerializeToArray(redisStreamBatchContainer);
        var base64String = Convert.ToBase64String(rawBytes);

        NameValueEntry namespaceEntry = new("namespace", streamId.Namespace);
        NameValueEntry keyEntry = new("key", streamId.Key);
        NameValueEntry dataEntry = new("data", (RedisValue)base64String);

        return new StreamEntry(RedisValue.Null, [namespaceEntry, keyEntry, dataEntry ]);
    }
}
