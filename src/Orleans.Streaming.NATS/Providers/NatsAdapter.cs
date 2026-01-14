using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.Streaming.NATS;

internal sealed class NatsAdapter(
    string providerName,
    NatsOptions options,
    ILoggerFactory loggerFactory,
    Serializer serializer,
    NatsConnectionManager natsConnectionManager) : IQueueAdapter
{
    public string Name => providerName;
    public bool IsRewindable => false; // We will make it rewindable later
    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        NatsQueueAdapterReceiver.Create(providerName, loggerFactory, natsConnectionManager, queueId.GetNumericId(),
            options, serializer);

    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        var batchContainer = new NatsBatchContainer(streamId, events.Cast<object>().ToArray(), requestContext);
        var raw = serializer.GetSerializer<NatsBatchContainer>().SerializeToArray(batchContainer);

        await natsConnectionManager.EnqueueMessage(new NatsStreamMessage
        {
            StreamId = streamId, Payload = raw, RequestContext = requestContext
        });
    }
}