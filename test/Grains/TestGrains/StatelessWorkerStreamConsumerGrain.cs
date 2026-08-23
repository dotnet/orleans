using System.Collections.Concurrent;
using Orleans.Concurrency;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Orleans.Streams.Core;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public sealed class StatelessWorkerStreamConsumerState
    {
        private readonly ConcurrentDictionary<Guid, byte> _observerActivations = new();
        private DeliveryRun? _currentRun;

        public DeliveryRun StartRun(string deliveryId, int expectedDeliveries, bool blockDeliveries = false)
        {
            var run = new DeliveryRun(deliveryId, expectedDeliveries, blockDeliveries);
            Volatile.Write(ref _currentRun, run);
            return run;
        }

        internal void RecordObserver(Guid activationId) => _observerActivations.TryAdd(activationId, 0);

        internal Task RecordDelivery(Guid activationId, string deliveryId)
        {
            var run = Volatile.Read(ref _currentRun);
            return run is not null && string.Equals(run.DeliveryId, deliveryId, StringComparison.Ordinal)
                ? run.RecordDelivery(activationId, _observerActivations.ContainsKey(activationId))
                : Task.CompletedTask;
        }

        public sealed class DeliveryRun
        {
            private readonly ConcurrentDictionary<Guid, int> _deliveryCounts = new();
            private readonly ConcurrentDictionary<Guid, int> _observerCounts = new();
            private readonly TaskCompletionSource _deliveriesReleased = CreateCompletionSource();
            private readonly TaskCompletionSource _deliveryRelease = CreateCompletionSource();
            private readonly TaskCompletionSource _deliveryTargetReached = CreateCompletionSource();
            private readonly bool _blockDeliveries;
            private readonly int _expectedDeliveries;
            private int _deliveryCount;
            private int _waitingDeliveryCount;

            internal DeliveryRun(string deliveryId, int expectedDeliveries, bool blockDeliveries)
            {
                DeliveryId = deliveryId;
                _expectedDeliveries = expectedDeliveries;
                _blockDeliveries = blockDeliveries;
            }

            internal string DeliveryId { get; }

            public int DeliveryCount => Volatile.Read(ref _deliveryCount);

            public int DeliveryActivationCount => _deliveryCounts.Count;

            public int ObserverActivationCount => _observerCounts.Count;

            public int WaitingDeliveryCount => Volatile.Read(ref _waitingDeliveryCount);

            public Task WaitForDeliveriesAsync(TimeSpan timeout) => _deliveryTargetReached.Task.WaitAsync(timeout);

            public async Task ReleaseDeliveriesAsync(TimeSpan timeout)
            {
                _deliveryRelease.TrySetResult();
                if (WaitingDeliveryCount == 0)
                {
                    _deliveriesReleased.TrySetResult();
                }

                await _deliveriesReleased.Task.WaitAsync(timeout);
            }

            internal async Task RecordDelivery(Guid activationId, bool observerAttached)
            {
                _deliveryCounts.AddOrUpdate(activationId, 1, static (_, count) => count + 1);
                if (observerAttached)
                {
                    _observerCounts.AddOrUpdate(activationId, 1, static (_, count) => count + 1);
                }

                var deliveryCount = Interlocked.Increment(ref _deliveryCount);
                if (!_blockDeliveries)
                {
                    if (deliveryCount >= _expectedDeliveries)
                    {
                        _deliveryTargetReached.TrySetResult();
                    }
                    return;
                }

                if (Interlocked.Increment(ref _waitingDeliveryCount) >= _expectedDeliveries)
                {
                    _deliveryTargetReached.TrySetResult();
                }

                try
                {
                    await _deliveryRelease.Task;
                }
                finally
                {
                    if (Interlocked.Decrement(ref _waitingDeliveryCount) == 0)
                    {
                        _deliveriesReleased.TrySetResult();
                    }
                }
            }
        }

        private static TaskCompletionSource CreateCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [StatelessWorker(MaxLocalWorkers)]
    public class StatelessWorkerStreamConsumerGrain
        : Grain, IStatelessWorkerStreamConsumerGrain, IStreamSubscriptionObserver, IAsyncObserver<string>
    {
        public const int MaxLocalWorkers = 4;
        public const string ExplicitStreamNamespace = "StatelessWorkerStreamingNamespace";

        private readonly Guid _activationId = Guid.NewGuid();
        private readonly StatelessWorkerStreamConsumerState _state;

        public StatelessWorkerStreamConsumerGrain(StatelessWorkerStreamConsumerState state)
        {
            _state = state;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;

        public Task OnNextAsync(string item, StreamSequenceToken? token = null) => _state.RecordDelivery(_activationId, item);

        public async Task BecomeConsumer(Guid[] streamIds, string providerToUse)
        {
            foreach (var streamId in streamIds)
            {
                var stream = this.GetStreamProvider(providerToUse).GetStream<string>(ExplicitStreamNamespace, streamId);
                _ = await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
                _state.RecordObserver(_activationId);
            }
        }

        public async Task BecomeConsumerFromToken(Guid streamId, string providerToUse)
        {
            var stream = this.GetStreamProvider(providerToUse).GetStream<string>(ExplicitStreamNamespace, streamId);
            _ = await stream.SubscribeAsync(this, new EventSequenceToken(0));
        }

        public async Task<int> StopConsuming(Guid streamId, string providerToUse)
        {
            var stream = this.GetStreamProvider(providerToUse).GetStream<string>(ExplicitStreamNamespace, streamId);
            var handles = await stream.GetAllSubscriptionHandles();
            foreach (var handle in handles)
            {
                await handle.UnsubscribeAsync();
            }

            return handles.Count;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            _state.RecordObserver(_activationId);
            await handleFactory.Create<string>().ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);
        }
    }

    [StatelessWorker(MaxLocalWorkers)]
    [ImplicitStreamSubscription(StreamNamespace)]
    public class ImplicitStatelessWorkerStreamConsumerGrain
        : Grain, IImplicitStatelessWorkerStreamConsumerGrain, IStreamSubscriptionObserver
    {
        public const int MaxLocalWorkers = 4;
        public const string StreamNamespace = "ImplicitStatelessWorkerStreamingNamespace";

        private readonly Guid _activationId = Guid.NewGuid();
        private readonly StatelessWorkerStreamConsumerState _state;

        public ImplicitStatelessWorkerStreamConsumerGrain(StatelessWorkerStreamConsumerState state)
        {
            _state = state;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            _state.RecordObserver(_activationId);
            await handleFactory.Create<string>().ResumeAsync(
                (item, token) => _state.RecordDelivery(_activationId, item),
                static exception => Task.CompletedTask,
                static () => Task.CompletedTask);
        }
    }

    [StatelessWorker]
    public class UnsupportedStatelessWorkerStreamConsumerGrain
        : Grain, IUnsupportedStatelessWorkerStreamConsumerGrain
    {
        public async Task BecomeConsumer(Guid streamId, string providerToUse)
        {
            var stream = this.GetStreamProvider(providerToUse)
                .GetStream<string>(StatelessWorkerStreamConsumerGrain.ExplicitStreamNamespace, streamId);
            _ = await stream.SubscribeAsync(static (item, token) => Task.CompletedTask);
        }
    }
}
