namespace Orleans.Streaming.AdoNet;

internal class AdoNetDataAdapter(Serializer<AdoNetBatchContainer> serializer) : IQueueDataAdapter<byte[], IBatchContainer>
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
}