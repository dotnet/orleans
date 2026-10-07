using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs;

internal interface IBufferedEventHubClient
{
    event Action<IReadOnlyList<EventData>>? BatchSucceeded;

    event Action<IReadOnlyList<EventData>, Exception>? BatchFailed;

    Task EnqueueEventAsync(EventData eventData, string partitionKey);

    Task<string[]> GetPartitionIdsAsync();

    Task CloseAsync(CancellationToken cancellationToken);
}
