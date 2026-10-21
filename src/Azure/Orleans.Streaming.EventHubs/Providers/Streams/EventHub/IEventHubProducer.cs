using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs;

internal interface IEventHubProducer
{
    Task SendAsync(EventData eventData, string partitionKey);

    Task<string[]> GetPartitionIdsAsync();

    Task CloseAsync(CancellationToken cancellationToken);
}
