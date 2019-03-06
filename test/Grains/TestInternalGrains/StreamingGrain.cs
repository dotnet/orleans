using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;

namespace UnitTests.Grains
{
    [Serializable]
    public class StreamItem
    {
        public string       Data;
        public Guid         StreamId;

        public StreamItem(string data, Guid streamId)
        {
            Data = data;
            StreamId = streamId;
        }

        public override string ToString()
        {
            return String.Format("{0}", Data);
        }
    }

    [Serializable]
    public class ConsumerObserver : IAsyncObserver<StreamItem>, IConsumerObserver
    {
        [NonSerialized]
        private ILogger _logger;
        [NonSerialized]
        private StreamSubscriptionHandle<StreamItem> _subscription;
        private int _itemsConsumed;
        private Guid _streamId;
        private string _streamNamespace;

        public Task<int> ItemsConsumed
        {
            get { return Task.FromResult(_itemsConsumed); }
        }

        private ConsumerObserver(ILogger logger)
        {
            _logger = logger;
            _itemsConsumed = 0;
        }

        public static ConsumerObserver NewObserver(ILogger logger)
        {
            if (null == logger)
                throw new ArgumentNullException("logger");
            return new ConsumerObserver(logger);
        }

        public Task OnNextAsync(StreamItem item, StreamSequenceToken token = null)
        {
            if (!item.StreamId.Equals(_streamId))
            {
                string excStr = String.Format("ConsumerObserver.OnNextAsync: received an item from the wrong stream." + 
                        " Got item {0} from stream = {1}, expecting stream = {2}, numConsumed={3}", 
                        item, item.StreamId, _streamId, _itemsConsumed);
                _logger.Error(0, excStr);
                throw new ArgumentException(excStr);
            }
            ++_itemsConsumed;

            string str = String.Format("ConsumerObserver.OnNextAsync: streamId={0}, item={1}, numConsumed={2}{3}", 
                _streamId, item.Data, _itemsConsumed, token != null ? ", token = " + token : "");
            if (ProducerObserver.DEBUG_STREAMING_GRAINS)
            {
                _logger.Info(str);
            }
            else
            {
                _logger.Debug(str);
            }
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {            
            _logger.Info("ConsumerObserver.OnCompletedAsync");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {            
            _logger.Info("ConsumerObserver.OnErrorAsync: ex={0}", ex);
            return Task.CompletedTask;
        }

        public async Task BecomeConsumer(Guid streamId, IStreamProvider streamProvider, string streamNamespace)
        {
            _logger.Info("BecomeConsumer");
            if (ProviderName != null)
                throw new InvalidOperationException("redundant call to BecomeConsumer");
            _streamId = streamId;
            ProviderName = streamProvider.Name;
            _streamNamespace = string.IsNullOrWhiteSpace(streamNamespace) ? null : streamNamespace.Trim();
            IAsyncStream<StreamItem> stream = streamProvider.GetStream<StreamItem>(streamId, streamNamespace);

            _subscription = await stream.SubscribeAsync(this);    
        }

        public async Task RenewConsumer(ILogger logger, IStreamProvider streamProvider)
        {
            _logger = logger;
            _logger.Info("RenewConsumer");
            IAsyncStream<StreamItem> stream = streamProvider.GetStream<StreamItem>(_streamId, _streamNamespace);
            _subscription = await stream.SubscribeAsync(this);
        }

        public async Task StopBeingConsumer(IStreamProvider streamProvider)
        {
            _logger.Info("StopBeingConsumer");
            if (_subscription != null)
            {
                await _subscription.UnsubscribeAsync();
                //_subscription.Dispose();
                _subscription = null;
            }
        }

        public Task<int> ConsumerCount
        {
            get { return Task.FromResult(_subscription == null ? 0 : 1); }
        }

        public string ProviderName { get; private set; }
    }

    [Serializable]
    public class ProducerObserver : IProducerObserver
    {
        [NonSerialized]
        private ILogger _logger;
        [NonSerialized]
        private IAsyncObserver<StreamItem> _observer;
        [NonSerialized]
        private Dictionary<IDisposable, TimerState> _timers;

        private int _itemsProduced;
        private int _expectedItemsProduced;
        private Guid _streamId;
        private string _streamNamespace;
        private string _providerName;
        private readonly InterlockedFlag _cleanedUpFlag;
        [NonSerialized]
        private bool _observerDisposedYet;
        [NonSerialized]
        private readonly IGrainFactory _grainFactory;

        public static bool DEBUG_STREAMING_GRAINS = true;

        private ProducerObserver(ILogger logger, IGrainFactory grainFactory)
        {
            _logger = logger;
            _observer = null;
            _timers = new Dictionary<IDisposable, TimerState>();

            _itemsProduced = 0;
            _expectedItemsProduced = 0;
            _streamId = default(Guid);
            _providerName = null;
            _cleanedUpFlag = new InterlockedFlag();
            _observerDisposedYet = false;
            _grainFactory = grainFactory;
        }

        public static ProducerObserver NewObserver(ILogger logger, IGrainFactory grainFactory)
        {
            if (null == logger)
                throw new ArgumentNullException("logger");
            return new ProducerObserver(logger, grainFactory);
        }

        public void BecomeProducer(Guid streamId, IStreamProvider streamProvider, string streamNamespace)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            _logger.Info("BecomeProducer");
            IAsyncStream<StreamItem> stream = streamProvider.GetStream<StreamItem>(streamId, streamNamespace);
            _observer = stream;
            var observerAsSMSProducer = _observer as SimpleMessageStreamProducer<StreamItem>;
            // only SimpleMessageStreamProducer implements IDisposable and a means to verify it was cleaned up.
            if (null == observerAsSMSProducer)
            {
                _logger.Info("ProducerObserver.BecomeProducer: producer requires no disposal; test short-circuited.");
                _observerDisposedYet = true;
            }
            else
            {
                _logger.Info("ProducerObserver.BecomeProducer: producer performs disposal during finalization.");
                observerAsSMSProducer.OnDisposeTestHook += 
                    () => 
                        _observerDisposedYet = true;
            }
            _streamId = streamId;
            _streamNamespace = string.IsNullOrWhiteSpace(streamNamespace) ? null : streamNamespace.Trim();
            _providerName = streamProvider.Name;
        }

        public void RenewProducer(ILogger logger, IStreamProvider streamProvider)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            _logger = logger;
            _logger.Info("RenewProducer");
            IAsyncStream<StreamItem> stream = streamProvider.GetStream<StreamItem>(_streamId, _streamNamespace);
            _observer = stream;
            var observerAsSMSProducer = _observer as SimpleMessageStreamProducer<StreamItem>;
            // only SimpleMessageStreamProducer implements IDisposable and a means to verify it was cleaned up.
            if (null == observerAsSMSProducer)
            {
                //_logger.Info("ProducerObserver.BecomeProducer: producer requires no disposal; test short-circuited.");
                _observerDisposedYet = true;
            }
            else
            {
                //_logger.Info("ProducerObserver.BecomeProducer: producer performs disposal during finalization.");
                observerAsSMSProducer.OnDisposeTestHook +=
                    () =>
                        _observerDisposedYet = true;
            }
        }

        private async Task<bool> ProduceItem(string data)
        {
            if (_cleanedUpFlag.IsSet)
                return false;

            StreamItem item = new StreamItem(data, _streamId);
            await _observer.OnNextAsync(item);
            _itemsProduced++;
            string str = String.Format("ProducerObserver.ProduceItem: streamId={0}, data={1}, numProduced so far={2}.", _streamId, data, _itemsProduced);
            if (DEBUG_STREAMING_GRAINS)
            {
                _logger.Info(str);
            }
            else
            {
                _logger.Debug(str);
            }
            return true;
        }

        public async Task ProduceSequentialSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            if (0 >= count)
                throw new ArgumentOutOfRangeException("count", "The count must be greater than zero.");
            _expectedItemsProduced += count;
            _logger.Info("ProducerObserver.ProduceSequentialSeries: streamId={0}, num items to produce={1}.", _streamId, count);
            for (var i = 1; i <= count; ++i)
                await ProduceItem(String.Format("sequential#{0}", i));
        }

        public Task ProduceParallelSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            if (0 >= count)
                throw new ArgumentOutOfRangeException("count", "The count must be greater than zero.");
            _logger.Info("ProducerObserver.ProduceParallelSeries: streamId={0}, num items to produce={1}.", _streamId, count);
            _expectedItemsProduced += count;
            var tasks = new Task<bool>[count];
            for (var i = 1; i <= count; ++i)
            {
                int capture = i;
                Func<Task<bool>> func = async () => 
                    { 
                        return await ProduceItem(String.Format("parallel#{0}", capture)); 
                    };
                // Need to call on different threads to force parallel execution.
                tasks[capture - 1] = Task.Factory.StartNew(func).Unwrap();
            }
            return Task.WhenAll(tasks);
        }

        public Task<int> ItemsProduced
        {
            get
            {
                _cleanedUpFlag.ThrowNotInitializedIfSet();
                return Task.FromResult(_itemsProduced);
            }
        }

        public Task ProducePeriodicSeries(Func<Func<object, Task>, IDisposable> createTimerFunc, int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            _logger.Info("ProducerObserver.ProducePeriodicSeries: streamId={0}, num items to produce={1}.", _streamId, count);
            var timer = TimerState.NewTimer(createTimerFunc, ProduceItem, RemoveTimer, count);
            // we can't pass the TimerState object in as the argument-- it might be prematurely collected, so we root
            // it to this object via the _timers dictionary.
            _timers.Add(timer.Handle, timer);
            _expectedItemsProduced += count;
            timer.StartTimer();
            return Task.CompletedTask;        
        }

        private void RemoveTimer(IDisposable handle)
        {
            _logger.Info("ProducerObserver.RemoveTimer: streamId={0}.", _streamId);
            if (handle == null)
                throw new ArgumentNullException("handle");
            if (!_timers.Remove(handle))
                throw new InvalidOperationException("handle not found");
        }

        public Task<Guid> StreamId
        {
            get
            {
                _cleanedUpFlag.ThrowNotInitializedIfSet();
                return Task.FromResult(_streamId);
            }
        }

        public string ProviderName
        {
            get
            {
                _cleanedUpFlag.ThrowNotInitializedIfSet();
                return _providerName;
            }
        }

        public Task AddNewConsumerGrain(Guid consumerGrainId)
        {
            var grain = _grainFactory.GetGrain<IStreaming_ConsumerGrain>(consumerGrainId, "UnitTests.Grains.Streaming_ConsumerGrain");
            return grain.BecomeConsumer(_streamId, _providerName, _streamNamespace);
        }

        public Task<int> ExpectedItemsProduced
        {
            get
            {
                _cleanedUpFlag.ThrowNotInitializedIfSet();
                return Task.FromResult(_expectedItemsProduced);
            }
        }

        public Task<int> ProducerCount
        {
            get { return Task.FromResult(_cleanedUpFlag.IsSet ? 0 : 1); }
        }

        public Task StopBeingProducer()
        {
            _logger.Info("StopBeingProducer");
            if (!_cleanedUpFlag.TrySet())
                return Task.CompletedTask;

            if (_timers != null)
            {
                foreach (var i in _timers)
                {
                    try
                    {
                        i.Value.Dispose();
                    }
                    catch (Exception exc)
                    {
                        _logger.Error(1, "StopBeingProducer: Timer Dispose() has thrown", exc);
                    }
                }
                _timers = null;
            }
            _observer = null; // Disposing
            return Task.CompletedTask;
        }

        public async Task VerifyFinished()
        {
            _logger.Info("ProducerObserver.VerifyFinished: waiting for observer disposal; streamId={0}", _streamId);
            while (!_observerDisposedYet)
            {
                await Task.Delay(1000);
                GC.Collect();
                GC.WaitForPendingFinalizers(); 
            }
            _logger.Info("ProducerObserver.VerifyFinished: observer disposed; streamId={0}", _streamId);
        }

        private class TimerState : IDisposable
        {
            private bool _started;
            public IDisposable Handle { get; private set; }
            private int _counter;
            private readonly Func<string, Task<bool>> _produceItemFunc;
            private readonly Action<IDisposable> _onDisposeFunc;
            private readonly InterlockedFlag _disposedFlag;

            private TimerState(Func<string, Task<bool>> produceItemFunc, Action<IDisposable> onDisposeFunc, int count)
            {
                _produceItemFunc = produceItemFunc;
                _onDisposeFunc = onDisposeFunc;
                _counter = count;
                _disposedFlag = new InterlockedFlag();
            }

            public static TimerState NewTimer(Func<Func<object, Task>, IDisposable> startTimerFunc, Func<string, Task<bool>> produceItemFunc, Action<IDisposable> onDisposeFunc, int count)
            {
                if (null == startTimerFunc)
                    throw new ArgumentNullException("startTimerFunc");
                if (null == produceItemFunc)
                    throw new ArgumentNullException("produceItemFunc");
                if (null == onDisposeFunc)
                    throw new ArgumentNullException("onDisposeFunc");
                if (0 >= count)
                    throw new ArgumentOutOfRangeException("count", count, "argument must be > 0");
                var newOb = new TimerState(produceItemFunc, onDisposeFunc, count);
                newOb.Handle = startTimerFunc(newOb.OnTickAsync);
                if (null == newOb.Handle)
                    throw new InvalidOperationException("startTimerFunc must not return null");
                return newOb;
            }

            public void StartTimer()
            {
                _disposedFlag.ThrowDisposedIfSet(GetType());

                if (_started)
                    throw new InvalidOperationException("timer already started");
                _started = true;
            }

            private async Task OnTickAsync(object unused)
            {
                if (_started && !_disposedFlag.IsSet)
                {
                    --_counter;
                    bool shouldContinue = await _produceItemFunc(String.Format("periodic#{0}", _counter));
                    if (!shouldContinue || 0 == _counter)
                        Dispose();
                }
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!_disposedFlag.TrySet())
                    return;
                _onDisposeFunc(Handle);
                Handle.Dispose();
                Handle = null;
            }
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class Streaming_ProducerGrain : Grain<Streaming_ProducerGrain_State>, IStreaming_ProducerGrain
    {
        private ILogger _logger;
        protected List<IProducerObserver> _producers;
        private InterlockedFlag _cleanedUpFlag;

        public override Task OnActivateAsync()
        {
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Streaming_ProducerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("OnActivateAsync");
             _producers = new List<IProducerObserver>();
            _cleanedUpFlag = new InterlockedFlag();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public virtual Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            ProducerObserver producer = ProducerObserver.NewObserver(_logger, GrainFactory);
            producer.BecomeProducer(streamId, GetStreamProvider(providerToUse), streamNamespace);
            _producers.Add(producer);
            return Task.CompletedTask;
        }

        public virtual async Task ProduceSequentialSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            foreach (var producer in _producers)
            {
                await producer.ProduceSequentialSeries(count);
            } 
        }

        public virtual async Task ProduceParallelSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            await Task.WhenAll(_producers.Select(p => p.ProduceParallelSeries(count)).ToArray());
        }

        public virtual async Task ProducePeriodicSeries(int count)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();

            await Task.WhenAll(_producers.Select(p => p.ProducePeriodicSeries(timerCallback =>
                {
                    return RegisterTimer(timerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
                },count)).ToArray());
        }

        public virtual async Task<int> GetExpectedItemsProduced()
        {
            var tasks = _producers.Select(p => p.ExpectedItemsProduced).ToArray();
            int[] expectedItemsProduced = await Task.WhenAll(tasks);
            return expectedItemsProduced.Sum();
        }

        public virtual async Task<int> GetItemsProduced()
        {
            var tasks = _producers.Select(p => p.ItemsProduced).ToArray();
            int[] itemsProduced = await Task.WhenAll(tasks);
            return itemsProduced.Sum();
        }

        public virtual async Task AddNewConsumerGrain(Guid consumerGrainId)
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();
            await Task.WhenAll(_producers.Select(
                target =>
                    target.AddNewConsumerGrain(consumerGrainId)).ToArray());
        }

        public virtual async Task<int> GetProducerCount()
        {
            _cleanedUpFlag.ThrowNotInitializedIfSet();
            var tasks = _producers.Select(p => p.ProducerCount).ToArray();
            int[] producerCount = await Task.WhenAll(tasks);
            return producerCount.Sum();
        }

        public virtual async Task StopBeingProducer()
        {
            if (!_cleanedUpFlag.TrySet())
                return;

            var tasks = _producers.Select(p => p.StopBeingProducer()).ToArray();
            await Task.WhenAll(tasks);
        }

        public virtual async Task VerifyFinished()
        {
            var tasks = _producers.Select(p => p.VerifyFinished()).ToArray();
            await Task.WhenAll(tasks);
            _producers.Clear();
        }

        public virtual Task DeactivateProducerOnIdle()
        {
            _logger.Info("DeactivateProducerOnIdle");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class PersistentStreaming_ProducerGrain : Streaming_ProducerGrain, IStreaming_ProducerGrain
    {
        private ILogger _logger;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.PersistentStreaming_ProducerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("OnActivateAsync");
            if (State.Producers == null)
            {
                State.Producers = new List<IProducerObserver>();
                _producers = State.Producers;
            }
            else
            {
                foreach (var producer in State.Producers)
                {
                    producer.RenewProducer(_logger, GetStreamProvider(producer.ProviderName));
                    _producers.Add(producer);
                }
            }
        }

        public override Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        public override async Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            await base.BecomeProducer(streamId, providerToUse, streamNamespace);
            State.Producers = _producers;
            await WriteStateAsync();
        }

        public override async Task ProduceSequentialSeries(int count)
        {
            await base.ProduceParallelSeries(count);
            State.Producers = _producers;
            await WriteStateAsync();
        }

        public override async Task ProduceParallelSeries(int count)
        {
            await base.ProduceParallelSeries(count);
            State.Producers = _producers;
            await WriteStateAsync();
        }

        public override async Task StopBeingProducer()
        {
            await base.StopBeingProducer();
            State.Producers = _producers;
            await WriteStateAsync();
        }

        public override async Task VerifyFinished()
        {
            await base.VerifyFinished();
            await ClearStateAsync();
        }
    }
    
    [StorageProvider(ProviderName = "MemoryStore")]
    public class Streaming_ConsumerGrain : Grain<Streaming_ConsumerGrain_State>, IStreaming_ConsumerGrain    
    {
        private ILogger _logger;
        protected List<IConsumerObserver> _observers;
        private string _providerToUse;
        
        public override Task OnActivateAsync()
        {
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Streaming_ConsumerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("OnActivateAsync");    
            _observers = new List<IConsumerObserver>();
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async virtual Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _providerToUse = providerToUse;
            ConsumerObserver consumerObserver = ConsumerObserver.NewObserver(_logger);
            await consumerObserver.BecomeConsumer(streamId, GetStreamProvider(providerToUse), streamNamespace);
            _observers.Add(consumerObserver);
        }

        public virtual async Task<int> GetItemsConsumed()
        {
            var tasks = _observers.Select(p => p.ItemsConsumed).ToArray();
            int[] itemsConsumed = await Task.WhenAll(tasks);
            return itemsConsumed.Sum();
        }

        public virtual async Task<int> GetConsumerCount()
        {
            var tasks = _observers.Select(p => p.ConsumerCount).ToArray();
            int[] consumerCount = await Task.WhenAll(tasks);
            return consumerCount.Sum();
        }

        public virtual async Task StopBeingConsumer()
        {
            var tasks = _observers.Select(obs => obs.StopBeingConsumer(GetStreamProvider(_providerToUse))).ToArray();
            await Task.WhenAll(tasks);
            _observers.Clear();
        }

        public virtual Task DeactivateConsumerOnIdle()
        {
            _logger.Info("DeactivateConsumerOnIdle");

            Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(task => { _logger.Info("DeactivateConsumerOnIdle ContinueWith fired."); }).Ignore(); // .WithTimeout(TimeSpan.FromSeconds(2));
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class PersistentStreaming_ConsumerGrain : Streaming_ConsumerGrain, IPersistentStreaming_ConsumerGrain
    {
        private ILogger _logger;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.PersistentStreaming_ConsumerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("OnActivateAsync");

            if (State.Consumers == null)
            {
                State.Consumers = new List<IConsumerObserver>();
                _observers = State.Consumers;
            }
            else
            {
                foreach (var consumer in State.Consumers)
                {
                    await consumer.RenewConsumer(_logger, GetStreamProvider(consumer.ProviderName));
                    _observers.Add(consumer);
                }
            }
        }

        public override async Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            await base.OnDeactivateAsync();
        }

        public override async Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace)
        {
            await base.BecomeConsumer(streamId, providerToUse, streamNamespace);
            State.Consumers = _observers;
            await WriteStateAsync();
        }

        public override async Task StopBeingConsumer()
        {
            await base.StopBeingConsumer();
            State.Consumers = _observers;
            await WriteStateAsync();
        }
    }


    [Reentrant]
    public class Streaming_Reentrant_ProducerConsumerGrain : Streaming_ProducerConsumerGrain, IStreaming_Reentrant_ProducerConsumerGrain
    {
        private ILogger _logger;

        public override async Task OnActivateAsync()
        {
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Streaming_Reentrant_ProducerConsumerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId) ;
            _logger.Info("OnActivateAsync");
            await base.OnActivateAsync();
        }
    }

    public class Streaming_ProducerConsumerGrain : Grain, IStreaming_ProducerConsumerGrain
    {
        private ILogger _logger;
        private ProducerObserver _producer;
        private ConsumerObserver _consumer;
        private string _providerToUseForConsumer;

        public override Task OnActivateAsync()
        {
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Streaming_ProducerConsumerGrain " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("OnActivateAsync");
            return Task.CompletedTask;
        }
        public override Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public Task BecomeProducer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _producer = ProducerObserver.NewObserver(_logger, GrainFactory);
            _producer.BecomeProducer(streamId, GetStreamProvider(providerToUse), streamNamespace);
            return Task.CompletedTask;
        }

        public Task ProduceSequentialSeries(int count)
        {
            return _producer.ProduceSequentialSeries(count);
        }

        public Task ProduceParallelSeries(int count)
        {
            return _producer.ProduceParallelSeries(count);
        }

        public Task ProducePeriodicSeries(int count)
        {
            return _producer.ProducePeriodicSeries(timerCallback =>
            {
                return RegisterTimer(timerCallback, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
            }, count);
        }

        public Task<int> GetItemsProduced()
        {
            return _producer.ItemsProduced;
        }

        public Task AddNewConsumerGrain(Guid consumerGrainId)
        {
            return _producer.AddNewConsumerGrain(consumerGrainId);
        }

        public async Task BecomeConsumer(Guid streamId, string providerToUse, string streamNamespace)
        {
            _providerToUseForConsumer = providerToUse;
            _consumer = ConsumerObserver.NewObserver(this._logger);
            await _consumer.BecomeConsumer(streamId, GetStreamProvider(providerToUse), streamNamespace);
        }

        public async Task<int> GetItemsConsumed()
        {
            return await _consumer.ItemsConsumed;
        }

        public async Task<int> GetExpectedItemsProduced()
        {
            return await _producer.ExpectedItemsProduced;
        }

        public async Task<int> GetConsumerCount()
        {
            return await _consumer.ConsumerCount;
        }

        public async Task<int> GetProducerCount()
        {
            return await _producer.ProducerCount;
        }

        public async Task StopBeingConsumer()
        {
            await _consumer.StopBeingConsumer(GetStreamProvider(_providerToUseForConsumer));
            _consumer = null;
        }

        public async Task StopBeingProducer()
        {
            await _producer.StopBeingProducer();
        }

        public async Task VerifyFinished()
        {
            await _producer.VerifyFinished();
            _producer = null;
        }

        public Task DeactivateConsumerOnIdle()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public Task DeactivateProducerOnIdle()
        {
            _logger.Info("DeactivateProducerOnIdle");
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    public abstract class Streaming_ImplicitlySubscribedConsumerGrainBase : Grain
    {
        private ILogger _logger;
        private Dictionary<string, IConsumerObserver> _observers;
        
        public override async Task OnActivateAsync()
        {
            _logger = this.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Streaming_ImplicitConsumerGrain1 " + RuntimeIdentity + "/" + IdentityString + "/" + Data.ActivationId);
            _logger.Info("{0}.OnActivateAsync", GetType().FullName);    
            _observers = new Dictionary<string, IConsumerObserver>();
            // discuss: Note that we need to know the provider that will be used in advance. I think it would be beneficial if we specified the provider as an argument to ImplicitConsumerActivationAttribute.

            var activeStreamProviders = Runtime.ServiceProvider
                .GetService<IKeyedServiceCollection<string,IStreamProvider>>()
                .GetServices(Runtime.ServiceProvider)
                .Select(service => service.Key).ToList();
            await Task.WhenAll(activeStreamProviders.Select(stream => BecomeConsumer(this.GetPrimaryKey(), stream, "TestNamespace1")));
        }

        public override Task OnDeactivateAsync()
        {
            _logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task BecomeConsumer(Guid streamGuid, string providerToUse, string streamNamespace)
        {
            if (_observers.ContainsKey(providerToUse))
            {
                throw new InvalidOperationException(string.Format("consumer already established for provider {0}.", providerToUse));
            }

            if (string.IsNullOrWhiteSpace(streamNamespace))
            {
                throw new ArgumentException("namespace is required (must not be null or whitespace)", "streamNamespace");
            }

            ConsumerObserver consumerObserver = ConsumerObserver.NewObserver(_logger);
            await consumerObserver.BecomeConsumer(streamGuid, GetStreamProvider(providerToUse), streamNamespace);
            _observers[providerToUse] = consumerObserver;
        }

        public virtual async Task<int> GetItemsConsumed()
        {
            int result = 0;
            foreach (var o in _observers.Values)
            {
                result += await o.ItemsConsumed;
            }
            return result;
        }

        public virtual Task<int> GetConsumerCount()
        {
            // it's currently impossible to detect how many implicit consumers are being used, 
            // so we must resort to hard-wiring this grain to only use one provider's consumer at a time. 
            // this problem will continue until we require the provider's name to be apart of the implicit subscriber attribute identity.
            return Task.FromResult(1);
            /*
            int result = 0;
            foreach (var o in _observers.Values)
            {
                result += await o.ConsumerCount;
            }
            return result;
            */
        }

        public async Task StopBeingConsumer()
        {
            await Task.WhenAll(_observers.Select(i => i.Value.StopBeingConsumer(GetStreamProvider(i.Key))));
            _observers = null;
        }

        public Task DeactivateConsumerOnIdle()
        {
            _logger.Info("DeactivateConsumerOnIdle");

            Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(task => { _logger.Info("DeactivateConsumerOnIdle ContinueWith fired."); }).Ignore(); // .WithTimeout(TimeSpan.FromSeconds(2));
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }
    
    [ImplicitStreamSubscription("TestNamespace1")]
    public class Streaming_ImplicitlySubscribedConsumerGrain : Streaming_ImplicitlySubscribedConsumerGrainBase, IStreaming_ImplicitlySubscribedConsumerGrain
    {}
}