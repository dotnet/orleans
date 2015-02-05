using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;

namespace LoadTestGrains
{
    public class AvailableStreamProviders
    {
        public static readonly string[] All = { "OldMockStreamProvider", "MockStreamProvider" };
        public List<string> Loaded { get; private set; }

        public AvailableStreamProviders(Func<string,IStreamProvider> getStreamProvider)
        {
            if (getStreamProvider == null)
            {
                throw new ArgumentNullException("getStreamProvider");
            }

            Loaded = new List<string>();

            foreach (string streamProviderName in All)
            {
                IStreamProvider streamProvider;
                try
                {
                    streamProvider = getStreamProvider(streamProviderName);
                }
                catch (KeyNotFoundException)
                {
                    continue;
                }
                if (streamProvider != null)
                {
                    Loaded.Add(streamProviderName);
                }
            }
        }
    }

    public class ConsumerGrain<TEvent> : Grain, IImplicitConsumerGrain
    {
        // the following value must be smaller than the SharedEventCounter's report period.
        private static readonly TimeSpan ReportPeriod = TimeSpan.FromSeconds(3);

        private Logger _logger;
        private IAsyncStream<TEvent> _stream;
        private StreamSubscriptionHandle<TEvent> _subscription;
        private AsyncObserver _observer;
        private long _numConsumed;
        private IDisposable _timer;

        protected async Task SubscribeAsync(string streamProviderName, Guid streamId, string streamNamespace, StreamSequenceToken token)
        {
            // set up subscriber logic.
            IStreamProvider provider = GetStreamProvider(streamProviderName);
            _stream = provider.GetStream<TEvent>(streamId, streamNamespace);
            _observer = new AsyncObserver(this);
            _subscription = await _stream.SubscribeAsync(_observer, token);

            SharedMemoryCounters.Add(SharedMemoryCounters.CounterIds.SubscriberCount, 1, _logger);
        }

        protected async Task UnsubscribeAsync()
        {
            if (_subscription == null)
            {
                return;
            }

            await _stream.UnsubscribeAsync(_subscription);
            _subscription = null;

            SharedMemoryCounters.Add(SharedMemoryCounters.CounterIds.SubscriberCount, -1, _logger);
        }

        public override Task OnActivateAsync()
        {
            _logger = GetLogger(GetType().Name);
            _numConsumed = 0;

            Random rng = new Random();
            TimeSpan startDelay = TimeSpan.FromMilliseconds(rng.Next((int)ReportPeriod.TotalMilliseconds));
            _timer = RegisterTimer(OnTimer, null, startDelay, ReportPeriod);

            return TaskDone.Done;
        }

        public override async Task OnDeactivateAsync()
        {
            if (_subscription != null)
            {
                await UnsubscribeAsync();
            }
        }

        protected virtual Task OnCompletedAsync()
        {
            throw new NotImplementedException();
        }

        protected virtual Task OnErrorAsync(Exception ex)
        {
            throw new NotImplementedException();
        }

        protected virtual Task OnNextAsync(TEvent item, StreamSequenceToken token = null)
        {
            ++_numConsumed;
            return TaskDone.Done;
        }

        private Task OnTimer(object __unused)
        {
            long quantity = _numConsumed;
            _numConsumed = 0;

            SharedMemoryCounters.Add(SharedMemoryCounters.CounterIds.EventsConsumed, quantity, _logger);
            // the subscriber count will never propigate unless we periodically touch it.
            SharedMemoryCounters.Add(SharedMemoryCounters.CounterIds.SubscriberCount, 0, _logger);

            return TaskDone.Done;
        }

        private class AsyncObserver : IAsyncObserver<TEvent>
        {
            private readonly ConsumerGrain<TEvent> _myGrain;

            public AsyncObserver(ConsumerGrain<TEvent> myGrain)
            {
                if (null == myGrain)
                {
                    throw new ArgumentNullException("myGrain");
                }

                _myGrain = myGrain;
            }

            public Task OnCompletedAsync()
            {
                return _myGrain.OnCompletedAsync();
            }

            public Task OnErrorAsync(Exception ex)
            {
                return _myGrain.OnErrorAsync(ex);
            }

            public Task OnNextAsync(TEvent item, StreamSequenceToken token = null)
            {
                return _myGrain.OnNextAsync(item, token);
            }
        }
    }

    [ImplicitStreamSubscription(StreamNamespace)]
    [Reentrant]
    public class ReentrantImplicitConsumerGrain : ConsumerGrain<StreamItem>
    {
        public const string StreamNamespace = "ReentrantImplicitConsumer";

        private AvailableStreamProviders _streamProviders;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            _streamProviders = new AvailableStreamProviders(GetStreamProvider);

            List<Task> subscribeTasks = new List<Task>(_streamProviders.Loaded.Count);
            foreach (string streamProviderName in _streamProviders.Loaded)
            {
                subscribeTasks.Add(SubscribeAsync(streamProviderName, this.GetPrimaryKey(), StreamNamespace, null));
            }
            await Task.WhenAll(subscribeTasks);
        }
    }

    [ImplicitStreamSubscription(StreamNamespace)]
    public class ImplicitConsumerGrain : ConsumerGrain<StreamingLoadTestBaseEvent>
    {
        public const string StreamNamespace = "ImplicitConsumer";
        private AvailableStreamProviders _streamProviders;
        private string _streamProviderName;

        private bool _additionalSubscriptionsCreated;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();

            _streamProviders = new AvailableStreamProviders(GetStreamProvider);

            List<Task> subscribeTasks = new List<Task>(_streamProviders.Loaded.Count);
            foreach (string streamProviderName in _streamProviders.Loaded)
            {
                subscribeTasks.Add(SubscribeAsync(streamProviderName, this.GetPrimaryKey(), StreamNamespace, null));
            }
            await Task.WhenAll(subscribeTasks);
        }

        protected override async Task OnNextAsync(StreamingLoadTestBaseEvent item, StreamSequenceToken token = null)
        {
            if (item is StreamingLoadTestStartEvent)
            {
                StreamingLoadTestStartEvent startItem = (StreamingLoadTestStartEvent)item;

                _streamProviderName = startItem.StreamProvider;
                List<Task> additionalSubscriptionTasks = null;
                if (!_additionalSubscriptionsCreated)
                {
                    _additionalSubscriptionsCreated = true;
                    additionalSubscriptionTasks = new List<Task>(startItem.AdditionalSubscribersCount);
                    for (int i = 0; i < startItem.AdditionalSubscribersCount; i++)
                    {
                        IExplicitConsumerGrain grain = ExplicitConsumerGrainFactory.GetGrain(Guid.NewGuid());
                        Task subscriptionTask = grain.Subscribe(this.GetPrimaryKey(), StreamNamespace, startItem, token);
                        additionalSubscriptionTasks.Add(subscriptionTask);
                    }
                }

                await item.TaskDelay();
                item.BusyWait();
                if (additionalSubscriptionTasks != null)
                {
                    await Task.WhenAll(additionalSubscriptionTasks);
                }
            }
            else if (item is StreamingLoadTestEvent)
            {
                await item.TaskDelay();
                item.BusyWait();
            }
            else if (item is StreamingLoadTestEndEvent)
            {
                await item.TaskDelay();
                item.BusyWait();

                await base.UnsubscribeAsync();
            }

            await base.OnNextAsync(item, token);
        }
    }
}