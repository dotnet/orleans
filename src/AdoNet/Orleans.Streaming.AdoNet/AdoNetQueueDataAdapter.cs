using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Streaming.AdoNet;

[SerializationCallbacks(typeof(OnDeserializedCallbacks))]
internal class AdoNetQueueDataAdapter(Serializer<AdoNetBatchContainer> serializer) : IQueueDataAdapter<byte[], IBatchContainer>, IOnDeserialized
{
    public byte[] ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        ArgumentNullException.ThrowIfNull(events);

        var container = new AdoNetBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var bytes = serializer.SerializeToArray(container);

        return bytes;
    }

    public IBatchContainer FromQueueMessage(byte[] queueMessage, long sequenceId)
    {
        ArgumentNullException.ThrowIfNull(queueMessage);

        var container = serializer.Deserialize(queueMessage);
        container.SequenceToken = new(sequenceId);
        return container;
    }

    public void OnDeserialized(DeserializationContext context) => serializer = context.ServiceProvider.GetRequiredService<Serializer<AdoNetBatchContainer>>();
}