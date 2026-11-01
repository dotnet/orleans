using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;

namespace Orleans.Streaming.EventHubs;

internal sealed class AcknowledgedEventHubProducer : IEventHubProducer
{
    private readonly IBufferedEventHubClient _client;
    private readonly ConcurrentDictionary<EventData, TaskCompletionSource> _pending =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _lock = new();
    private readonly TaskCompletionSource _enqueuesDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeEnqueues;
    private bool _closed;
    private Task? _closeTask;

    public AcknowledgedEventHubProducer(IBufferedEventHubClient client)
    {
        _client = client;
        _client.BatchSucceeded += OnBatchSucceeded;
        _client.BatchFailed += OnBatchFailed;
    }

    public async Task SendAsync(EventData eventData, string partitionKey)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_pending.TryAdd(eventData, completion))
            {
                throw new InvalidOperationException("The Event Hubs event is already pending publication.");
            }

            _activeEnqueues++;
        }

        try
        {
            await _client.EnqueueEventAsync(eventData, partitionKey);
        }
        catch
        {
            _pending.TryRemove(eventData, out _);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                if (--_activeEnqueues == 0 && _closed)
                {
                    _enqueuesDrained.TrySetResult();
                }
            }
        }

        await completion.Task;
    }

    public Task<string[]> GetPartitionIdsAsync() => _client.GetPartitionIdsAsync();

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return _closeTask ??= CloseCoreAsync();
        }
    }

    private async Task CloseCoreAsync()
    {
        Task enqueuesDrained;
        lock (_lock)
        {
            _closed = true;
            if (_activeEnqueues == 0)
            {
                _enqueuesDrained.TrySetResult();
            }

            enqueuesDrained = _enqueuesDrained.Task;
        }

        await enqueuesDrained;
        try
        {
            await _client.CloseAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            FailPending(exception);
            throw;
        }

        if (!_pending.IsEmpty)
        {
            var exception = new InvalidOperationException(
                "The Event Hubs producer closed before all buffered events received a publication result.");
            FailPending(exception);
            throw exception;
        }
    }

    private void OnBatchSucceeded(IReadOnlyList<EventData> eventBatch)
    {
        foreach (var eventData in eventBatch)
        {
            if (_pending.TryRemove(eventData, out var completion))
            {
                completion.TrySetResult();
            }
        }
    }

    private void OnBatchFailed(IReadOnlyList<EventData> eventBatch, Exception exception)
    {
        foreach (var eventData in eventBatch)
        {
            if (_pending.TryRemove(eventData, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var eventData in _pending.Keys)
        {
            if (_pending.TryRemove(eventData, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }
}
