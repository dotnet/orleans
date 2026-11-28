using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace Orleans.Streaming.EventHubs;

internal sealed class EventHubProducer(EventHubProducerClient client) : IEventHubProducer
{
    private int _closed;

    public Task SendAsync(EventData eventData, string partitionKey)
        => client.SendAsync([eventData], new SendEventOptions { PartitionKey = partitionKey });

    public Task<string[]> GetPartitionIdsAsync() => client.GetPartitionIdsAsync();

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await client.CloseAsync(cancellationToken);
    }
}
