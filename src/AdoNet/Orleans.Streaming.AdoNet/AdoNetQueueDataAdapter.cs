using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Streaming.AdoNet;

/// <summary>
/// Implements a <see cref="IQueueDataAdapter{TQueueMessage, TMessageBatch}"/> for the ADO.NET streaming provider.
/// </summary>
[GenerateSerializer]
[SerializationCallbacks(typeof(OnDeserializedCallbacks))]
[Alias("Orleans.Streaming.AdoNet.AdoNetQueueDataAdapter")]
internal class AdoNetQueueDataAdapter(Serializer<AdoNetBatchContainer> serializer) : IQueueDataAdapter<byte[], IBatchContainer>, IOnDeserialized
{
    [NonSerialized]
    private Serializer<AdoNetBatchContainer> _serializer = serializer;

    public byte[] ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        ArgumentNullException.ThrowIfNull(events);

        var container = new AdoNetBatchContainer(streamId, events.Cast<object>().ToList(), requestContext);
        var bytes = _serializer.SerializeToArray(container);

        return bytes;
    }

    public IBatchContainer FromQueueMessage(byte[] queueMessage, long sequenceId)
    {
        ArgumentNullException.ThrowIfNull(queueMessage);

        var container = _serializer.Deserialize(queueMessage);
        container.SequenceToken = new(sequenceId);
        return container;
    }

    public void OnDeserialized(DeserializationContext context) => _serializer = context.ServiceProvider.GetRequiredService<Serializer<AdoNetBatchContainer>>();
}