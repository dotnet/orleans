using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace Orleans.Streaming.EventHubs;

internal sealed class BufferedEventHubClient : IBufferedEventHubClient
{
    private readonly EventHubBufferedProducerClient _client;
    private int _closed;

    public BufferedEventHubClient(
        EventHubConnection connection,
        EventHubBufferedProducerClientOptions options)
    {
        _client = new EventHubBufferedProducerClient(connection, options);
        _client.SendEventBatchSucceededAsync += OnBatchSucceededAsync;
        _client.SendEventBatchFailedAsync += OnBatchFailedAsync;
    }

    public event Action<IReadOnlyList<EventData>>? BatchSucceeded;

    public event Action<IReadOnlyList<EventData>, Exception>? BatchFailed;

    public async Task EnqueueEventAsync(EventData eventData, string partitionKey)
    {
        var options = new EnqueueEventOptions { PartitionKey = partitionKey };
        await _client.EnqueueEventAsync(eventData, options);
    }

    public Task<string[]> GetPartitionIdsAsync() => _client.GetPartitionIdsAsync();

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _client.CloseAsync(flush: true, cancellationToken);
    }

    private Task OnBatchSucceededAsync(SendEventBatchSucceededEventArgs args)
    {
        BatchSucceeded?.Invoke(args.EventBatch);
        return Task.CompletedTask;
    }

    private Task OnBatchFailedAsync(SendEventBatchFailedEventArgs args)
    {
        BatchFailed?.Invoke(args.EventBatch, args.Exception);
        return Task.CompletedTask;
    }
}
