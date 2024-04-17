using Orleans.Runtime;

namespace Orleans.Streaming.AdoNet;

internal class AdoNetDataAdapter : IQueueDataAdapter<byte[], IBatchContainer>
{
    public byte[] ToQueueMessage<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token, Dictionary<string, object> requestContext)
    {
        throw new NotImplementedException();
    }

    public IBatchContainer FromQueueMessage(byte[] queueMessage, long sequenceId)
    {
        throw new NotImplementedException();
    }
}