#nullable enable

using Orleans.Runtime;
using Orleans.Streaming.Diagnostics;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

internal sealed class StreamingDiagnosticsProbeSystemTarget : SystemTarget, IStreamingDiagnosticsProbe, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly StreamingDiagnosticEventRecorder _recorder;

    public StreamingDiagnosticsProbeSystemTarget(
        StreamingDiagnosticEventRecorder recorder,
        SystemTargetShared shared)
        : base(StreamingDiagnosticsProbeConstants.SystemTargetType, shared)
    {
        _recorder = recorder;
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
    }

    public Task<SiloAddress> GetLocation()
        => Task.FromResult(Silo);

    public Task WaitForProviderReady(string providerName, int expectedQueueCount, TimeSpan timeout)
        => _recorder.WaitForProviderReady(providerName, expectedQueueCount, timeout);

    public Task WaitForProducerRegistered(string providerName, StreamId streamId, TimeSpan timeout)
        => _recorder.WaitForProducerRegistered(providerName, streamId, timeout);

    public Task WaitForPullingAgentStreamRegistered(string providerName, StreamId streamId, TimeSpan timeout)
        => _recorder.WaitForPullingAgentStreamRegistered(providerName, streamId, timeout);

    public Task WaitForSubscriptionRegistered(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => _recorder.WaitForSubscriptionRegistered(providerName, streamId, subscriptionId, timeout);

    public Task WaitForSubscriptionAttached(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => _recorder.WaitForSubscriptionAttached(providerName, streamId, subscriptionId, timeout);

    public Task<int> GetItemDeliveredCount(string providerName, StreamId streamId, Guid subscriptionId)
        => Task.FromResult(_recorder.GetItemDeliveredCount(providerName, streamId, subscriptionId));

    public Task WaitForItemDelivered(string providerName, StreamId streamId, Guid subscriptionId, int expectedCount, TimeSpan timeout)
        => _recorder.WaitForItemDelivered(providerName, streamId, subscriptionId, expectedCount, timeout);

    public Task WaitForConsumerCursorDrained(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => _recorder.WaitForConsumerCursorDrained(providerName, streamId, subscriptionId, timeout);

    public Task<string> GetRecentStreamingDiagnostics() => Task.FromResult(_recorder.GetSummary());
}

public sealed class StreamingDiagnosticEventRecorder(
    ILocalSiloDetails localSiloDetails) : IStartupTask, IDisposable
{
    private const int MaxRecentEvents = 64;
    private const int MaxSummaryItems = 8;

    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<QueueId>> _startedQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<QueueId>> _initializedQueues = new(StringComparer.Ordinal);
    private readonly HashSet<StreamKey> _producerRegistrations = [];
    private readonly HashSet<StreamKey> _pullingAgentStreamRegistrations = [];
    private readonly HashSet<SubscriptionKey> _subscriptionRegistrations = [];
    private readonly HashSet<SubscriptionKey> _subscriptionAttachments = [];
    private readonly Dictionary<SubscriptionKey, int> _itemDeliveries = [];
    private readonly HashSet<SubscriptionKey> _cursorDrains = [];
    private readonly Queue<string> _recentEvents = new();
    private readonly List<Waiter> _waiters = [];
    private readonly SiloAddress _localSiloAddress = localSiloDetails.SiloAddress;

    private IDisposable? _subscription;

    public Task Execute(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _subscription ??= StreamingEvents.AllEvents.Subscribe(new Observer(this));
        }

        return Task.CompletedTask;
    }

    public Task WaitForProviderReady(string providerName, int expectedQueueCount, TimeSpan timeout)
        => WaitUntil(
            () => QueueCount(_startedQueues, providerName) >= expectedQueueCount
                && QueueCount(_initializedQueues, providerName) >= expectedQueueCount,
            $"provider '{providerName}' to have {expectedQueueCount} started and initialized queue(s)",
            timeout);

    public Task WaitForProducerRegistered(string providerName, StreamId streamId, TimeSpan timeout)
        => WaitUntil(
            () => _producerRegistrations.Contains(new(providerName, streamId)),
            $"producer registration for provider '{providerName}' stream '{streamId}'",
            timeout);

    public Task WaitForPullingAgentStreamRegistered(string providerName, StreamId streamId, TimeSpan timeout)
        => WaitUntil(
            () => _pullingAgentStreamRegistrations.Contains(new(providerName, streamId)),
            $"pulling-agent stream registration for provider '{providerName}' stream '{streamId}'",
            timeout);

    public Task WaitForSubscriptionRegistered(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => WaitUntil(
            () => _subscriptionRegistrations.Contains(new(providerName, streamId, subscriptionId)),
            $"subscription '{subscriptionId}' registration for provider '{providerName}' stream '{streamId}'",
            timeout);

    public Task WaitForSubscriptionAttached(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => WaitUntil(
            () => _subscriptionAttachments.Contains(new(providerName, streamId, subscriptionId)),
            $"subscription '{subscriptionId}' attachment for provider '{providerName}' stream '{streamId}'",
            timeout);

    public Task WaitForItemDelivered(string providerName, StreamId streamId, Guid subscriptionId, int expectedCount, TimeSpan timeout)
        => WaitUntil(
            () => _itemDeliveries.GetValueOrDefault(new(providerName, streamId, subscriptionId)) >= expectedCount,
            $"{expectedCount} item delivery event(s) for subscription '{subscriptionId}' provider '{providerName}' stream '{streamId}'",
            timeout);

    public int GetItemDeliveredCount(string providerName, StreamId streamId, Guid subscriptionId)
    {
        lock (_lock)
        {
            return _itemDeliveries.GetValueOrDefault(new(providerName, streamId, subscriptionId));
        }
    }

    public Task WaitForConsumerCursorDrained(string providerName, StreamId streamId, Guid subscriptionId, TimeSpan timeout)
        => WaitUntil(
            () => _cursorDrains.Contains(new(providerName, streamId, subscriptionId)),
            $"cursor drain for subscription '{subscriptionId}' provider '{providerName}' stream '{streamId}'",
            timeout);

    public string GetSummary()
    {
        lock (_lock)
        {
            return GetSummaryUnsafe();
        }
    }

    public void Dispose() => _subscription?.Dispose();

    private Task WaitUntil(Func<bool> condition, string description, TimeSpan timeout)
    {
        Waiter waiter;
        lock (_lock)
        {
            if (condition())
            {
                return Task.CompletedTask;
            }

            waiter = new(condition, description);
            _waiters.Add(waiter);
        }

        return WaitWithTimeout(waiter, timeout);
    }

    private async Task WaitWithTimeout(Waiter waiter, TimeSpan timeout)
    {
        try
        {
            await waiter.Task.WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            lock (_lock)
            {
                _waiters.Remove(waiter);
                throw new TimeoutException($"Timed out waiting for {waiter.Description}. {GetSummaryUnsafe()}", exception);
            }
        }
    }

    internal void OnEvent(StreamingEvents.StreamingEvent evt)
    {
        if (evt.SiloAddress is { } siloAddress && !siloAddress.Equals(_localSiloAddress))
        {
            return;
        }

        List<Waiter>? completedWaiters = null;

        lock (_lock)
        {
            switch (evt)
            {
                case StreamingEvents.BalancerChanged e:
                    SetQueues(_startedQueues, e.StreamProvider, e.CurrentQueues);
                    AddRecent($"BalancerChanged provider={e.StreamProvider} silo={e.SiloAddress} currentQueues={e.CurrentQueues.Length}");
                    break;
                case StreamingEvents.PullingAgentStarted e:
                    AddQueue(_startedQueues, e.StreamProvider, e.QueueId);
                    AddRecent($"PullingAgentStarted provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId}");
                    break;
                case StreamingEvents.PullingAgentStopped e:
                    RemoveQueue(_startedQueues, e.StreamProvider, e.QueueId);
                    RemoveQueue(_initializedQueues, e.StreamProvider, e.QueueId);
                    AddRecent($"PullingAgentStopped provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId}");
                    break;
                case StreamingEvents.QueueReceiverInitialized e:
                    AddQueue(_initializedQueues, e.StreamProvider, e.QueueId);
                    AddRecent($"QueueReceiverInitialized provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId}");
                    break;
                case StreamingEvents.QueueReceiverInitializationFailed e:
                    AddRecent($"QueueReceiverInitializationFailed provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId} exception={e.Exception.GetType().Name}: {e.Exception.Message}");
                    break;
                case StreamingEvents.ProducerRegistered e:
                    _producerRegistrations.Add(new(e.StreamProvider, e.StreamId));
                    AddRecent($"ProducerRegistered provider={e.StreamProvider} silo={e.SiloAddress} stream={e.StreamId}");
                    break;
                case StreamingEvents.PullingAgentStreamRegistered e:
                    _pullingAgentStreamRegistrations.Add(new(e.StreamProvider, e.StreamId));
                    AddRecent($"PullingAgentStreamRegistered provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId} stream={e.StreamId} subscribers={e.SubscriberCount}");
                    break;
                case StreamingEvents.PullingAgentStreamRegistrationFailed e:
                    AddRecent($"PullingAgentStreamRegistrationFailed provider={e.StreamProvider} silo={e.SiloAddress} queue={e.QueueId} stream={e.StreamId} exception={e.Exception.GetType().Name}: {e.Exception.Message}");
                    break;
                case StreamingEvents.SubscriptionRegistered e:
                    _subscriptionRegistrations.Add(new(e.StreamProvider, e.StreamId, e.SubscriptionId));
                    AddRecent($"SubscriptionRegistered provider={e.StreamProvider} silo={e.SiloAddress} stream={e.StreamId} subscription={e.SubscriptionId}");
                    break;
                case StreamingEvents.SubscriptionAttached e:
                    _subscriptionAttachments.Add(new(e.StreamProvider, e.StreamId, e.SubscriptionId));
                    AddRecent($"SubscriptionAttached provider={e.StreamProvider} silo={e.SiloAddress} stream={e.StreamId} subscription={e.SubscriptionId}");
                    break;
                case StreamingEvents.ItemDelivered e:
                    var itemKey = new SubscriptionKey(e.StreamProvider, e.StreamId, e.SubscriptionId);
                    _itemDeliveries[itemKey] = _itemDeliveries.GetValueOrDefault(itemKey) + 1;
                    AddRecent($"ItemDelivered provider={e.StreamProvider} silo={e.SiloAddress} stream={e.StreamId} subscription={e.SubscriptionId} count={_itemDeliveries[itemKey]}");
                    break;
                case StreamingEvents.ConsumerCursorDrained e:
                    _cursorDrains.Add(new(e.StreamProvider, e.StreamId, e.SubscriptionId));
                    AddRecent($"ConsumerCursorDrained provider={e.StreamProvider} silo={e.SiloAddress} stream={e.StreamId} subscription={e.SubscriptionId}");
                    break;
            }

            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                var waiter = _waiters[i];
                if (waiter.Condition())
                {
                    _waiters.RemoveAt(i);
                    completedWaiters ??= [];
                    completedWaiters.Add(waiter);
                }
            }
        }

        if (completedWaiters is not null)
        {
            foreach (var waiter in completedWaiters)
            {
                waiter.Complete();
            }
        }
    }

    private string GetSummaryUnsafe()
    {
        var started = string.Join(", ", _startedQueues.Select(kvp => $"{kvp.Key}:{kvp.Value.Count}"));
        var initialized = string.Join(", ", _initializedQueues.Select(kvp => $"{kvp.Key}:{kvp.Value.Count}"));
        var recent = string.Join(" | ", _recentEvents);
        return $"StartedQueues=[{started}] "
            + $"InitializedQueues=[{initialized}] "
            + $"ProducerRegistrations=[{FormatCollection(_producerRegistrations)}] "
            + $"PullingAgentStreamRegistrations=[{FormatCollection(_pullingAgentStreamRegistrations)}] "
            + $"SubscriptionRegistrations=[{FormatCollection(_subscriptionRegistrations)}] "
            + $"SubscriptionAttachments=[{FormatCollection(_subscriptionAttachments)}] "
            + $"ItemDeliveries=[{FormatDictionary(_itemDeliveries)}] "
            + $"CursorDrains=[{FormatCollection(_cursorDrains)}] "
            + $"RecentEvents=[{recent}]";
    }

    private void AddRecent(string message)
    {
        if (_recentEvents.Count == MaxRecentEvents)
        {
            _recentEvents.Dequeue();
        }

        _recentEvents.Enqueue(message);
    }

    private static int QueueCount(Dictionary<string, HashSet<QueueId>> queuesByProvider, string providerName)
    {
        return queuesByProvider.TryGetValue(providerName, out var queues) ? queues.Count : 0;
    }

    private static void AddQueue(Dictionary<string, HashSet<QueueId>> queuesByProvider, string providerName, QueueId queueId)
    {
        if (!queuesByProvider.TryGetValue(providerName, out var queues))
        {
            queuesByProvider[providerName] = queues = [];
        }

        queues.Add(queueId);
    }

    private static void RemoveQueue(Dictionary<string, HashSet<QueueId>> queuesByProvider, string providerName, QueueId queueId)
    {
        if (queuesByProvider.TryGetValue(providerName, out var queues))
        {
            queues.Remove(queueId);
        }
    }

    private static void SetQueues(Dictionary<string, HashSet<QueueId>> queuesByProvider, string providerName, IEnumerable<QueueId> queues)
    {
        queuesByProvider[providerName] = [.. queues];
    }

    private static string FormatCollection<T>(IReadOnlyCollection<T> values)
    {
        var suffix = values.Count > MaxSummaryItems ? ", ..." : string.Empty;
        return $"Count={values.Count}; Items=[{string.Join(", ", values.Take(MaxSummaryItems))}{suffix}]";
    }

    private static string FormatDictionary<TKey>(IReadOnlyDictionary<TKey, int> values)
        where TKey : notnull
    {
        var suffix = values.Count > MaxSummaryItems ? ", ..." : string.Empty;
        var entries = values
            .Take(MaxSummaryItems)
            .Select(static kvp => $"{kvp.Key}:{kvp.Value}");
        return $"Count={values.Count}; Items=[{string.Join(", ", entries)}{suffix}]";
    }

    private sealed class Waiter(Func<bool> condition, string description)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<bool> Condition { get; } = condition;
        public string Description { get; } = description;
        public Task Task => _completion.Task;

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class Observer(StreamingDiagnosticEventRecorder recorder) : IObserver<StreamingEvents.StreamingEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(StreamingEvents.StreamingEvent value) => recorder.OnEvent(value);
    }

    private readonly record struct StreamKey(string ProviderName, StreamId StreamId);
    private readonly record struct SubscriptionKey(string ProviderName, StreamId StreamId, Guid SubscriptionId);
}
