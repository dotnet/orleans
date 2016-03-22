//#define USE_GENERICS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;

namespace UnitTests.Grains
{
    public class StreamReliabilityTestGrainState
    {
        // For producer and consumer 
        // -- only need to store because of how we run our unit tests against multiple providers
        public string StreamProviderName { get; set; }

        // For producer only.
#if USE_GENERICS
        public IAsyncStream<T> Stream { get; set; }
#else
        public IAsyncStream<int> Stream { get; set; }
#endif

        public bool IsProducer { get; set; }

        // For consumer only.
#if USE_GENERICS
        public HashSet<StreamSubscriptionHandle<T>> ConsumerSubscriptionHandles { get; set; }

        public StreamReliabilityTestGrainState()
        {
            ConsumerSubscriptionHandles = new HashSet<StreamSubscriptionHandle<T>>();
        }
#else
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
        private Logger logger;

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

        public override async Task OnActivateAsync()
        {
            logger = GetLogger("StreamReliabilityTestGrain-" + this.IdentityString);
            logger.Info(String.Format("OnActivateAsync IsProducer = {0}, IsConsumer = {1}.",
                State.IsProducer, State.ConsumerSubscriptionHandles != null && State.ConsumerSubscriptionHandles.Count > 0));

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
                logger.Info("No stream yet.");
            }
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return base.OnDeactivateAsync();
        }

        public Task<int> GetConsumerCount()
        {
            int numConsumers = State.ConsumerSubscriptionHandles.Count;
            logger.Info("ConsumerCount={0}", numConsumers);
            return Task.FromResult(numConsumers);
        }
        public Task<int> GetReceivedCount()
        {
            int numReceived = Observers.Sum(o => o.Value.NumItems);
            logger.Info("ReceivedCount={0}", numReceived);
            return Task.FromResult(numReceived);
        }
        public Task<int> GetErrorsCount()
        {
            int numErrors = Observers.Sum(o => o.Value.NumErrors);
            logger.Info("ErrorsCount={0}", numErrors);
            return Task.FromResult(numErrors);
        }

        public Task Ping()
        {
            logger.Info("Ping");
            return TaskDone.Done;
        }

#if USE_GENERICS
        public async Task<StreamSubscriptionHandle<T>> AddConsumer(Guid streamId, string providerName)
#else
        public async Task<StreamSubscriptionHandle<int>> AddConsumer(Guid streamId, string providerName)
#endif
        {
            logger.Info("AddConsumer StreamId={0} StreamProvider={1} Grain={2}", streamId, providerName, this.AsReference<IStreamReliabilityTestGrain>());
            TryInitStream(streamId, providerName);
#if USE_GENERICS
            var observer = new MyStreamObserver<T>();
#else
            var observer = new MyStreamObserver<int>(logger);
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
            logger.Info("RemoveConsumer StreamId={0} StreamProvider={1}", streamId, providerName);
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            await subsHandle.UnsubscribeAsync();
            Observers.Remove(subsHandle);
            State.ConsumerSubscriptionHandles.Remove(subsHandle);
            await WriteStateAsync();
        }

        public async Task RemoveAllConsumers()
        {
            logger.Info("RemoveAllConsumers: State.ConsumerSubscriptionHandles.Count={0}", State.ConsumerSubscriptionHandles.Count);
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
            logger.Info("BecomeProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            TryInitStream(streamId, providerName);
            Producer = Stream;
            State.IsProducer = true;
            await WriteStateAsync();
        }

        public async Task RemoveProducer(Guid streamId, string providerName)
        {
            logger.Info("RemoveProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            if (!State.IsProducer) throw new InvalidOperationException("Not a Producer");
            Producer = null;
            State.IsProducer = false;
            await WriteStateAsync();
        }

        public async Task ClearGrain()
        {
            logger.Info("ClearGrain.");
            State.ConsumerSubscriptionHandles.Clear();
            State.IsProducer = false;
            Observers.Clear();
            State.Stream = null;
            await ClearStateAsync();
        }

        public Task<bool> IsConsumer()
        {
            bool isConsumer = State.ConsumerSubscriptionHandles.Count > 0;
            logger.Info("IsConsumer={0}", isConsumer);
            return Task.FromResult(isConsumer);
        }
        public Task<bool> IsProducer()
        {
            bool isProducer = State.IsProducer;
            logger.Info("IsProducer={0}", isProducer);
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
            logger.Info("SendItem Item={0}", item);
            await Producer.OnNextAsync(item);
        }

        public Task<SiloAddress> GetLocation()
        {
            SiloAddress siloAddress = Data.Address.Silo;
            logger.Info("GetLocation SiloAddress={0}", siloAddress);
            return Task.FromResult(siloAddress);
        }

        private void TryInitStream(Guid streamId, string providerName)
        {
            Assert.IsNotNull(streamId, "Can't have null stream id");
            Assert.IsNotNull(providerName, "Can't have null stream provider name");

            State.StreamProviderName = providerName;

            if (State.Stream == null)
            {
                logger.Info("InitStream StreamId={0} StreamProvider={1}", streamId, providerName);

                IStreamProvider streamProvider = GetStreamProvider(providerName);
#if USE_GENERICS
                State.Stream = streamProvider.GetStream<T>(streamId);
#else
                State.Stream = streamProvider.GetStream<int>(streamId, StreamNamespace);
#endif
            }
        }

#if USE_GENERICS
        private async Task ReconnectConsumerHandles(StreamSubscriptionHandle<T>[] subscriptionHandles)
#else
        private async Task ReconnectConsumerHandles(StreamSubscriptionHandle<int>[] subscriptionHandles)
#endif
        {
            logger.Info("ReconnectConsumerHandles SubscriptionHandles={0} Grain={1}", Utils.EnumerableToString(subscriptionHandles), this.AsReference<IStreamReliabilityTestGrain>());


            foreach (var subHandle in subscriptionHandles)
            {
#if USE_GENERICS
                // var stream = GetStreamProvider(State.StreamProviderName).GetStream<T>(subHandle.StreamId);
                var stream = subHandle.Stream;
                var observer = new MyStreamObserver<T>();
#else
                var observer = new MyStreamObserver<int>(logger);
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
    //        return TaskDone.Done;
    //    }

    //    public Task OnCompletedAsync()
    //    {
    //        logger.Info("Receive OnCompletedAsync - Total Items={0} Errors={1}", NumItems, NumErrors);
    //        return TaskDone.Done;
    //    }

    //    public Task OnErrorAsync(Exception ex)
    //    {
    //        NumErrors++;
    //        logger.Warn(1, "Received OnErrorAsync - Exception={0} - Total Items={1} Errors={2}", ex, NumItems, NumErrors);
    //        return TaskDone.Done;
    //    }
    //}


    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class StreamUnsubscribeTestGrain : Grain<StreamReliabilityTestGrainState>, IStreamUnsubscribeTestGrain
    {
        [NonSerialized]
        private Logger logger;

        private const string StreamNamespace = StreamTestsConstants.StreamReliabilityNamespace;

        public override Task OnActivateAsync()
        {
            logger = GetLogger("StreamUnsubscribeTestGrain-" + this.IdentityString);
            logger.Info(String.Format("OnActivateAsync IsProducer = {0}, IsConsumer = {1}.",
                State.IsProducer, State.ConsumerSubscriptionHandles != null && State.ConsumerSubscriptionHandles.Count > 0));
            return TaskDone.Done;
        }

        public async Task Subscribe(Guid streamId, string providerName)
        {
            logger.Info("Subscribe StreamId={0} StreamProvider={1} Grain={2}", streamId, providerName, this.AsReference<IStreamUnsubscribeTestGrain>());

            State.StreamProviderName = providerName;
            if (State.Stream == null)
            {
                logger.Info("InitStream StreamId={0} StreamProvider={1}", streamId, providerName);
                IStreamProvider streamProvider = GetStreamProvider(providerName);
                State.Stream = streamProvider.GetStream<int>(streamId, StreamNamespace);
            }

            var observer = new MyStreamObserver<int>(logger);
            var consumer = State.Stream;
            var subsHandle = await consumer.SubscribeAsync(observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
            await WriteStateAsync();
        }

        public async Task UnSubscribeFromAllStreams()
        {
            logger.Info("UnSubscribeFromAllStreams: State.ConsumerSubscriptionHandles.Count={0}", State.ConsumerSubscriptionHandles.Count);
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