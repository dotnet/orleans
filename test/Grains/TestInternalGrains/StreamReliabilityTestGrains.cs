//#define USE_GENERICS

using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class StreamReliabilityTestGrainState
    {
        // For producer and consumer
        // -- only need to store because of how we run our unit tests against multiple providers
        [Id(0)]
        public string StreamProviderName { get; set; }

        // For producer only.
#if USE_GENERICS
        public IAsyncStream<T> Stream { get; set; }
#else
        [Id(1)]
        public IAsyncStream<int> Stream { get; set; }
#endif

        [Id(2)]
        public bool IsProducer { get; set; }

        // For consumer only.
#if USE_GENERICS
        public HashSet<StreamSubscriptionHandle<T>> ConsumerSubscriptionHandles { get; set; }

        public StreamReliabilityTestGrainState()
        {
            ConsumerSubscriptionHandles = new HashSet<StreamSubscriptionHandle<T>>();
        }
#else
        [Id(3)]
        public HashSet<StreamSubscriptionHandle<int>> ConsumerSubscriptionHandles { get; set; }

        public StreamReliabilityTestGrainState()
        {
            ConsumerSubscriptionHandles = new HashSet<StreamSubscriptionHandle<int>>();
        }
#endif
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
#if USE_GENERICS
    public class StreamReliabilityTestGrain<T> : Grain<IStreamReliabilityTestGrainState>, IStreamReliabilityTestGrain<T>
#else
    public class StreamReliabilityTestGrain : Grain<StreamReliabilityTestGrainState>, IStreamReliabilityTestGrain
#endif
    {
        [NonSerialized]
        private readonly ILogger _logger;

        private readonly IGrainContext _grainContext;

#if USE_GENERICS
        private IAsyncStream<T> Stream { get; set; }
        private IAsyncObserver<T> Producer { get; set; }
        private Dictionary<StreamSubscriptionHandle<T>, MyStreamObserver<T>> Observers { get; set; }
#else
        private IAsyncStream<int> Stream { get { return State.Stream; } }
        private IAsyncObserver<int> Producer { get; set; }
        private Dictionary<StreamSubscriptionHandle<int>, MyStreamObserver<int>> Observers { get; set; }
#endif
        private const string StreamNamespace = StreamTestsConstants.StreamReliabilityNamespace;

        public StreamReliabilityTestGrain(ILoggerFactory loggerFactory, IGrainContext grainContext)
        {
            _grainContext = grainContext;
            this._logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "OnActivateAsync IsProducer = {IsProducer}, IsConsumer = {IsConsumer}.",
                State.IsProducer,
                State.ConsumerSubscriptionHandles is { Count: > 0 });

            if (Observers == null)
#if USE_GENERICS
                Observers = new Dictionary<StreamSubscriptionHandle<T>, MyStreamObserver<T>>();
#else
                Observers = new Dictionary<StreamSubscriptionHandle<int>, MyStreamObserver<int>>();
#endif

            if (State.Stream != null && State.StreamProviderName != null)
            {
                //TryInitStream(State.Stream, State.StreamProviderName);

                if (State.ConsumerSubscriptionHandles.Count > 0)
                {
                    var handles = State.ConsumerSubscriptionHandles.ToArray();
                    State.ConsumerSubscriptionHandles.Clear();
                    await ReconnectConsumerHandles(handles);
                }
                if (State.IsProducer)
                {
                    //await BecomeProducer(State.StreamId, State.StreamProviderName);
                    Producer = Stream;
                    State.IsProducer = true;
                    await WriteStateAsync();
                }
            }
            else
            {
                _logger.LogInformation("No stream yet.");
            }
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            _logger.LogInformation("OnDeactivateAsync");
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<int> GetConsumerCount()
        {
            int numConsumers = State.ConsumerSubscriptionHandles.Count;
            _logger.LogInformation("ConsumerCount={Count}", numConsumers);
            return Task.FromResult(numConsumers);
        }
        public Task<int> GetReceivedCount()
        {
            int numReceived = Observers.Sum(o => o.Value.NumItems);
            _logger.LogInformation("ReceivedCount={Count}", numReceived);
            return Task.FromResult(numReceived);
        }
        public Task<int> GetErrorsCount()
        {
            int numErrors = Observers.Sum(o => o.Value.NumErrors);
            _logger.LogInformation("ErrorsCount={Count}", numErrors);
            return Task.FromResult(numErrors);
        }

        public Task Ping()
        {
            _logger.LogInformation("Ping");
            return Task.CompletedTask;
        }

#if USE_GENERICS
        public async Task<StreamSubscriptionHandle<T>> AddConsumer(Guid streamId, string providerName)
#else
        public async Task<StreamSubscriptionHandle<int>> AddConsumer(Guid streamId, string providerName)
#endif
        {
            _logger.LogInformation("AddConsumer StreamId={StreamId} StreamProvider={ProviderName} Grain={Grain}", streamId, providerName, this.AsReference<IStreamReliabilityTestGrain>());
            TryInitStream(streamId, providerName);
#if USE_GENERICS
            var observer = new MyStreamObserver<T>();
#else
            var observer = new MyStreamObserver<int>(_logger);
#endif
            var subsHandle = await Stream.SubscribeAsync(observer);
            Observers.Add(subsHandle, observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
            await WriteStateAsync();
            return subsHandle;
        }

#if USE_GENERICS
        public async Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<T> subsHandle)
#else
        public async Task RemoveConsumer(Guid streamId, string providerName, StreamSubscriptionHandle<int> subsHandle)
#endif
        {
            _logger.LogInformation("RemoveConsumer StreamId={StreamId} StreamProvider={ProviderName}", streamId, providerName);
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            await subsHandle.UnsubscribeAsync();
            Observers.Remove(subsHandle);
            State.ConsumerSubscriptionHandles.Remove(subsHandle);
            await WriteStateAsync();
        }

        public async Task RemoveAllConsumers()
        {
            _logger.LogInformation("RemoveAllConsumers: State.ConsumerSubscriptionHandles.Count={Count}", State.ConsumerSubscriptionHandles.Count);
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            var handles = State.ConsumerSubscriptionHandles.ToArray();
            foreach (var handle in handles)
            {
                await handle.UnsubscribeAsync();
            }
            //Observers.Remove(subsHandle);
            State.ConsumerSubscriptionHandles.Clear();
            await WriteStateAsync();
        }

        public async Task BecomeProducer(Guid streamId, string providerName)
        {
            _logger.LogInformation("BecomeProducer StreamId={StreamId} StreamProvider={StreamProvider}", streamId, providerName);
            TryInitStream(streamId, providerName);
            Producer = Stream;
            State.IsProducer = true;
            await WriteStateAsync();
        }

        public async Task RemoveProducer(Guid streamId, string providerName)
        {
            _logger.LogInformation("RemoveProducer StreamId={StreamId} StreamProvider={ProviderName}", streamId, providerName);
            if (!State.IsProducer) throw new InvalidOperationException("Not a Producer");
            Producer = null;
            State.IsProducer = false;
            await WriteStateAsync();
        }

        public async Task ClearGrain()
        {
            _logger.LogInformation("ClearGrain.");
            State.ConsumerSubscriptionHandles.Clear();
            State.IsProducer = false;
            Observers.Clear();
            State.Stream = null;
            await ClearStateAsync();
        }

        public Task<bool> IsConsumer()
        {
            bool isConsumer = State.ConsumerSubscriptionHandles.Count > 0;
            _logger.LogInformation("IsConsumer={IsConsumer}", isConsumer);
            return Task.FromResult(isConsumer);
        }
        public Task<bool> IsProducer()
        {
            bool isProducer = State.IsProducer;
            _logger.LogInformation("IsProducer={IsProducer}", isProducer);
            return Task.FromResult(isProducer);
        }
        public Task<int> GetConsumerHandlesCount()
        {
            return Task.FromResult(State.ConsumerSubscriptionHandles.Count);
        }

        public async Task<int> GetConsumerObserversCount()
        {
#if USE_GENERICS
            var consumer = (StreamConsumer<T>)Stream;
#else
            var consumer = (StreamConsumer<int>)Stream;
#endif
            return await consumer.DiagGetConsumerObserversCount();
        }


#if USE_GENERICS
        public async Task SendItem(T item)
#else
        public async Task SendItem(int item)
#endif
        {
            _logger.LogInformation("SendItem Item={Item}", item);
            await Producer.OnNextAsync(item);
        }

        public Task<SiloAddress> GetLocation()
        {
            SiloAddress siloAddress = _grainContext.Address.SiloAddress;
            _logger.LogInformation("GetLocation SiloAddress={SiloAddress}", siloAddress);
            return Task.FromResult(siloAddress);
        }

        private void TryInitStream(Guid streamId, string providerName)
        {
            if (providerName == null) throw new ArgumentNullException(nameof(providerName));

            State.StreamProviderName = providerName;

            if (State.Stream == null)
            {
                _logger.LogInformation("InitStream StreamId={StreamId} StreamProvider={ProviderName}", streamId, providerName);

                IStreamProvider streamProvider = this.GetStreamProvider(providerName);
#if USE_GENERICS
                State.Stream = streamProvider.GetStream<T>(streamId);
#else
                State.Stream = streamProvider.GetStream<int>(StreamNamespace, streamId);
#endif
            }
        }

#if USE_GENERICS
        private async Task ReconnectConsumerHandles(StreamSubscriptionHandle<T>[] subscriptionHandles)
#else
        private async Task ReconnectConsumerHandles(StreamSubscriptionHandle<int>[] subscriptionHandles)
#endif
        {
            _logger.LogInformation(
                "ReconnectConsumerHandles SubscriptionHandles={SubscriptionHandles} Grain={Grain}",
                Utils.EnumerableToString(subscriptionHandles),
                this.AsReference<IStreamReliabilityTestGrain>());


            foreach (var subHandle in subscriptionHandles)
            {
#if USE_GENERICS
                // var stream = GetStreamProvider(State.StreamProviderName).GetStream<T>(subHandle.StreamId);
                var stream = subHandle.Stream;
                var observer = new MyStreamObserver<T>();
#else
                var observer = new MyStreamObserver<int>(_logger);
#endif
                var subsHandle = await subHandle.ResumeAsync(observer);
                Observers.Add(subsHandle, observer);
                State.ConsumerSubscriptionHandles.Add(subsHandle);
            }
            await WriteStateAsync();
        }
    }

    //[Serializable]
    //public class MyStreamObserver<T> : IAsyncObserver<T>
    //{
    //    internal int NumItems { get; private set; }
    //    internal int NumErrors { get; private set; }

    //    private readonly Logger logger;

    //    internal MyStreamObserver(Logger logger)
    //    {
    //        this.logger = logger;
    //    }

    //    public Task OnNextAsync(T item, StreamSequenceToken token)
    //    {
    //        NumItems++;
    //        if (logger.IsVerbose)
    //            logger.Verbose("Received OnNextAsync - Item={0} - Total Items={1} Errors={2}", item, NumItems, NumErrors);
    //        return Task.CompletedTask;
    //    }

    //    public Task OnCompletedAsync()
    //    {
    //        logger.Info("Receive OnCompletedAsync - Total Items={0} Errors={1}", NumItems, NumErrors);
    //        return Task.CompletedTask;
    //    }

    //    public Task OnErrorAsync(Exception ex)
    //    {
    //        NumErrors++;
    //        logger.Warn(1, "Received OnErrorAsync - Exception={0} - Total Items={1} Errors={2}", ex, NumItems, NumErrors);
    //        return Task.CompletedTask;
    //    }
    //}


    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class StreamUnsubscribeTestGrain : Grain<StreamReliabilityTestGrainState>, IStreamUnsubscribeTestGrain
    {
        [NonSerialized]
        private readonly ILogger _logger;

        private const string StreamNamespace = StreamTestsConstants.StreamReliabilityNamespace;

        public StreamUnsubscribeTestGrain(ILoggerFactory loggerFactory)
        {
            this._logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(string.Format("OnActivateAsync IsProducer = {0}, IsConsumer = {1}.",
                State.IsProducer, State.ConsumerSubscriptionHandles != null && State.ConsumerSubscriptionHandles.Count > 0));
            return Task.CompletedTask;
        }

        public async Task Subscribe(Guid streamId, string providerName)
        {
            _logger.LogInformation("Subscribe StreamId={StreamId} StreamProvider={ProviderName} Grain={Grain}", streamId, providerName, this.AsReference<IStreamUnsubscribeTestGrain>());

            State.StreamProviderName = providerName;
            if (State.Stream == null)
            {
                _logger.LogInformation("InitStream StreamId={StreamId} StreamProvider={ProviderName}", streamId, providerName);
                IStreamProvider streamProvider = this.GetStreamProvider(providerName);
                State.Stream = streamProvider.GetStream<int>(StreamNamespace, streamId);
            }

            var observer = new MyStreamObserver<int>(_logger);
            var consumer = State.Stream;
            var subsHandle = await consumer.SubscribeAsync(observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
            await WriteStateAsync();
        }

        public async Task UnSubscribeFromAllStreams()
        {
            _logger.LogInformation("UnSubscribeFromAllStreams: State.ConsumerSubscriptionHandles.Count={Count}", State.ConsumerSubscriptionHandles.Count);
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            var handles = State.ConsumerSubscriptionHandles.ToArray();
            foreach (var handle in handles)
            {
                await handle.UnsubscribeAsync();
            }
            State.ConsumerSubscriptionHandles.Clear();
            await WriteStateAsync();
        }
    }
}