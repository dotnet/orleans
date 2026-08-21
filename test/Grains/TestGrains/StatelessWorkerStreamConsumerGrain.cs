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
        private readonly ConcurrentDictionary<Guid, int> _deliveryCounts = new();
        private readonly ConcurrentDictionary<Guid, int> _observerCounts = new();
        private readonly SemaphoreSlim _deliverySemaphore = new(0);
        private TaskCompletionSource _deliveriesReleased = CreateCompletionSource();
        private TaskCompletionSource _deliveryTargetReached = CreateCompletionSource();
        private bool _blockDeliveries;
        private int _deliveryCount;
        private int _expectedDeliveries;
        private int _waitingDeliveryCount;

        public int DeliveryCount => Volatile.Read(ref _deliveryCount);

        public int DeliveryActivationCount => _deliveryCounts.Count;

        public int ObserverActivationCount => _observerCounts.Count;

        public int WaitingDeliveryCount => Volatile.Read(ref _waitingDeliveryCount);

        public void Reset(int expectedDeliveries, bool blockDeliveries = false)
        {
            if (WaitingDeliveryCount != 0)
            {
                throw new InvalidOperationException("All blocked stream deliveries must be released before resetting the test state.");
            }

            while (_deliverySemaphore.Wait(0))
            {
            }

            _deliveryCounts.Clear();
            _observerCounts.Clear();
            Interlocked.Exchange(ref _deliveryCount, 0);
            _expectedDeliveries = expectedDeliveries;
            Volatile.Write(ref _blockDeliveries, blockDeliveries);
            _deliveriesReleased = CreateCompletionSource();
            _deliveryTargetReached = CreateCompletionSource();
        }

        public Task WaitForDeliveriesAsync(TimeSpan timeout) => _deliveryTargetReached.Task.WaitAsync(timeout);

        public Task WaitForReleasedDeliveriesAsync(TimeSpan timeout) => _deliveriesReleased.Task.WaitAsync(timeout);

        public void ReleaseDeliveries() => _deliverySemaphore.Release(_expectedDeliveries);

        internal void RecordObserver(Guid activationId) => _observerCounts.AddOrUpdate(activationId, 1, static (_, count) => count + 1);

        internal async Task RecordDelivery(Guid activationId)
        {
            _deliveryCounts.AddOrUpdate(activationId, 1, static (_, count) => count + 1);
            var deliveryCount = Interlocked.Increment(ref _deliveryCount);
            if (!Volatile.Read(ref _blockDeliveries))
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
                await _deliverySemaphore.WaitAsync();
            }
            finally
            {
                if (Interlocked.Decrement(ref _waitingDeliveryCount) == 0)
                {
                    _deliveriesReleased.TrySetResult();
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

        public Task OnNextAsync(string item, StreamSequenceToken? token = null) => _state.RecordDelivery(_activationId);

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
                (item, token) => _state.RecordDelivery(_activationId),
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
