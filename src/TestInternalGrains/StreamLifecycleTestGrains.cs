#define COUNT_ACTIVATE_DEACTIVATE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class StreamLifecycleTestGrainState
    {
        // For producer and consumer 
        // -- only need to store this because of how we run our unit tests against multiple providers
        public string StreamProviderName { get; set; }

        // For producer only.
        public IAsyncStream<int> Stream { get; set; }
        public bool IsProducer { get; set; }
        public int NumMessagesSent { get; set; }
        public int NumErrors { get; set; }

        // For consumer only.
        public HashSet<StreamSubscriptionHandle<int>> ConsumerSubscriptionHandles { get; set; }

        public StreamLifecycleTestGrainState()
        {
            ConsumerSubscriptionHandles = new HashSet<StreamSubscriptionHandle<int>>();
        }
    }

    public class StreamLifecycleTestGrainBase : Grain<StreamLifecycleTestGrainState>
    {
        protected Logger logger;
        protected string _lastProviderName;
        protected IStreamProvider _streamProvider;

#if COUNT_ACTIVATE_DEACTIVATE
        private IActivateDeactivateWatcherGrain watcher;
#endif

        protected Task RecordActivate()
        {
#if COUNT_ACTIVATE_DEACTIVATE
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            return watcher.RecordActivateCall(IdentityString);
#else
            return TaskDone.Done;
#endif
        }

        protected Task RecordDeactivate()
        {
#if COUNT_ACTIVATE_DEACTIVATE
            return watcher.RecordDeactivateCall(IdentityString);
#else
            return TaskDone.Done;
#endif
        }

        protected void InitStream(Guid streamId, string streamNamespace, string providerToUse)
        {
            if (streamId == null) throw new ArgumentNullException("streamId", "Can't have null stream id");
            if (streamNamespace == null) throw new ArgumentNullException("streamNamespace", "Can't have null stream namespace values");
            if (providerToUse == null) throw new ArgumentNullException("providerToUse", "Can't have null stream provider name");

            if (State.Stream != null && State.Stream.Guid != streamId)
            {
                if (logger.IsVerbose) logger.Verbose("Stream already exists for StreamId={0} StreamProvider={1} - Resetting", State.Stream, providerToUse);

                // Note: in this test, we are deliberately not doing Unsubscribe consumers, just discard old stream and let auto-cleanup functions do their thing.
                State.ConsumerSubscriptionHandles.Clear();
                State.IsProducer = false;
                State.NumMessagesSent = 0;
                State.NumErrors = 0;
                State.Stream = null;
            }

            if (logger.IsVerbose) logger.Verbose("InitStream StreamId={0} StreamProvider={1}", streamId, providerToUse);

            if (providerToUse != _lastProviderName)
            {
                _streamProvider = GetStreamProvider(providerToUse);
                _lastProviderName = providerToUse;
            }
            IAsyncStream<int> stream = _streamProvider.GetStream<int>(streamId, streamNamespace);
            State.Stream = stream;
            State.StreamProviderName = providerToUse;

            if (logger.IsVerbose) logger.Verbose("InitStream returning with Stream={0} with ref type = {1}", State.Stream, State.Stream.GetType().FullName);
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StreamLifecycleConsumerGrain : StreamLifecycleTestGrainBase, IStreamLifecycleConsumerGrain
    {
        protected IDictionary<StreamSubscriptionHandle<int>, MyStreamObserver<int>> Observers { get; set; }

        public override async Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + "-" + IdentityString);
            if (logger.IsVerbose) logger.Verbose("OnActivateAsync");

            await RecordActivate();

            if (Observers == null)
            {
                Observers = new Dictionary<StreamSubscriptionHandle<int>, MyStreamObserver<int>>();
            }

            if (State.Stream != null && State.StreamProviderName != null)
            {
                if (State.ConsumerSubscriptionHandles.Count > 0)
                {
                    var handles = State.ConsumerSubscriptionHandles.ToArray();
                    logger.Info("ReconnectConsumerHandles SubscriptionHandles={0} Grain={1}", Utils.EnumerableToString(handles), this.AsReference<IStreamLifecycleConsumerGrain>());
                    foreach (var handle in handles)
                    {
                        var observer = new MyStreamObserver<int>(logger);
                        StreamSubscriptionHandle<int> subsHandle = await handle.ResumeAsync(observer);
                        Observers.Add(subsHandle, observer);
                    }
                }
            }
            else
            {
                if (logger.IsVerbose) logger.Verbose("Not conected to stream yet.");
            }
        }
        public override async Task OnDeactivateAsync()
        {
            if (logger.IsVerbose) logger.Verbose("OnDeactivateAsync");
            await RecordDeactivate();
        }

        public Task<int> GetReceivedCount()
        {
            int numReceived = Observers.Sum(o => o.Value.NumItems);
            if (logger.IsVerbose) logger.Verbose("ReceivedCount={0}", numReceived);
            return Task.FromResult(numReceived);
        }
        public Task<int> GetErrorsCount()
        {
            int numErrors = Observers.Sum(o => o.Value.NumErrors);
            if (logger.IsVerbose) logger.Verbose("ErrorsCount={0}", numErrors);
            return Task.FromResult(numErrors);
        }

        public Task Ping()
        {
            logger.Info("Ping");
            return TaskDone.Done;
        }

        public virtual async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            if (logger.IsVerbose) logger.Verbose("BecomeConsumer StreamId={0} StreamProvider={1} Grain={2}", streamId, providerToUse, this.AsReference<IStreamLifecycleConsumerGrain>());
            InitStream(streamId, streamNamespace, providerToUse);
            var observer = new MyStreamObserver<int>(logger);
            var subsHandle = await State.Stream.SubscribeAsync(observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
            Observers.Add(subsHandle, observer);
            await WriteStateAsync();
        }

        public virtual async Task TestBecomeConsumerSlim(Guid streamIdGuid, string streamNamespace, string providerName)
        {
            InitStream(streamIdGuid, streamNamespace, providerName);
            var observer = new MyStreamObserver<int>(logger);

            //var subsHandle = await State.Stream.SubscribeAsync(observer);

            IStreamConsumerExtension myExtensionReference;
#if USE_CAST
            myExtensionReference = StreamConsumerExtensionFactory.Cast(this.AsReference());
#else
            var tup = await SiloProviderRuntime.Instance.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                        () => new StreamConsumerExtension(SiloProviderRuntime.Instance));
            StreamConsumerExtension myExtension = tup.Item1;
            myExtensionReference = tup.Item2;
#endif
            string extKey = providerName + "_" + State.Stream.Namespace;
            IPubSubRendezvousGrain pubsub = GrainFactory.GetGrain<IPubSubRendezvousGrain>(streamIdGuid, extKey, null);
            GuidId subscriptionId = GuidId.GetNewGuidId();
            await pubsub.RegisterConsumer(subscriptionId, ((StreamImpl<int>)State.Stream).StreamId, myExtensionReference, null);

            myExtension.SetObserver(subscriptionId, ((StreamImpl<int>)State.Stream), observer, null, null);
        }

        public async Task RemoveConsumer(Guid streamId, string streamNamespace, string providerName, StreamSubscriptionHandle<int> subsHandle)
        {
            if (logger.IsVerbose) logger.Verbose("RemoveConsumer StreamId={0} StreamProvider={1}", streamId, providerName);
            if (State.ConsumerSubscriptionHandles.Count == 0) throw new InvalidOperationException("Not a Consumer");
            await subsHandle.UnsubscribeAsync();
            Observers.Remove(subsHandle);
            State.ConsumerSubscriptionHandles.Remove(subsHandle);
            await WriteStateAsync();
        }

        public async Task ClearGrain()
        {
            logger.Info("ClearGrain");
            var subsHandles = State.ConsumerSubscriptionHandles.ToArray();
            foreach (var handle in subsHandles)
            {
                await handle.UnsubscribeAsync();
            }
            State.ConsumerSubscriptionHandles.Clear();
            State.Stream = null;
            State.IsProducer = false;
            Observers.Clear();
            await ClearStateAsync();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class FilteredStreamConsumerGrain : StreamLifecycleConsumerGrain, IFilteredStreamConsumerGrain
    {
        private static Logger _logger;

        private const int FilterDataOdd = 1;
        private const int FilterDataEven = 2;

        public override Task BecomeConsumer(Guid streamId, string streamNamespace, string providerName)
        {
            throw new InvalidOperationException("Should not be calling unfiltered BecomeConsumer method on " + GetType());
        }
        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerName, bool sendEvensOnly)
        {
            _logger = logger;
            if (logger.IsVerbose)
                logger.Verbose("BecomeConsumer StreamId={0} StreamProvider={1} Filter={2} Grain={3}",
                streamId, providerName, sendEvensOnly, this.AsReference<IFilteredStreamConsumerGrain>());
            InitStream(streamId, streamNamespace, providerName);

            var observer = new MyStreamObserver<int>(logger);

            StreamFilterPredicate filterFunc;
            object filterData;
            if (sendEvensOnly)
            {
                filterFunc = FilterIsEven;
                filterData = FilterDataEven;
            }
            else
            {
                filterFunc = FilterIsOdd;
                filterData = FilterDataOdd;
            }

            var subsHandle = await State.Stream.SubscribeAsync(observer, null, filterFunc, filterData);

            State.ConsumerSubscriptionHandles.Add(subsHandle);
            Observers.Add(subsHandle, observer);
            await WriteStateAsync();
        }

        public async Task SubscribeWithBadFunc(Guid streamId, string streamNamespace, string providerName)
        {
            logger.Info("SubscribeWithBadFunc StreamId={0} StreamProvider={1}Grain={2}",
                streamId, providerName, this.AsReference<IFilteredStreamConsumerGrain>());

            InitStream(streamId, streamNamespace, providerName);

            var observer = new MyStreamObserver<int>(logger);

            StreamFilterPredicate filterFunc = BadFunc;

            // This next call should fail because func is not static
            await State.Stream.SubscribeAsync(observer, null, filterFunc);
        }

        public static bool FilterIsEven(IStreamIdentity stream, object filterData, object item)
        {
            if (!FilterDataEven.Equals(filterData))
            {
                throw new Exception("Should have got the correct filter data passed in, but got: " + filterData);
            }
            int val = (int) item;
            bool result = val % 2 == 0;
            if (_logger != null) _logger.Info("FilterIsEven(Stream={0},FilterData={1},Item={2}) Filter = {3}", stream, filterData, item, result);
            return result;
        }
        public static bool FilterIsOdd(IStreamIdentity stream, object filterData, object item)
        {
            if (!FilterDataOdd.Equals(filterData))
            {
                throw new Exception("Should have got the correct filter data passed in, but got: " + filterData);
            }
            int val = (int) item;
            bool result = val % 2 == 1;
            if (_logger != null) _logger.Info("FilterIsOdd(Stream={0},FilterData={1},Item={2}) Filter = {3}", stream, filterData, item, result);
            return result;
        }
        // Function is not static, so cannot be used as a filter predicate function.
        public bool BadFunc(IStreamIdentity stream, object filterData, object item)
        {
            return true;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StreamLifecycleProducerGrain : StreamLifecycleTestGrainBase, IStreamLifecycleProducerGrain
    {
        public override async Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + "-" + IdentityString);
            if (logger.IsVerbose) logger.Verbose("OnActivateAsync");

            await RecordActivate();

            if (State.Stream != null && State.StreamProviderName != null)
            {
                if (logger.IsVerbose) logger.Verbose("Reconnected to stream {0}", State.Stream);
            }
            else
            {
                if (logger.IsVerbose) logger.Verbose("Not connected to stream yet.");
            }
        }
        public override async Task OnDeactivateAsync()
        {
            if (logger.IsVerbose) logger.Verbose("OnDeactivateAsync");
            await RecordDeactivate();
        }

        public Task<int> GetSendCount()
        {
            int result = State.NumMessagesSent;
            if (logger.IsVerbose) logger.Verbose("GetSendCount={0}", result);
            return Task.FromResult(result);
        }

        public Task<int> GetErrorsCount()
        {
            int result = State.NumErrors;
            if (logger.IsVerbose) logger.Verbose("GetErrorsCount={0}", result);
            return Task.FromResult(result);
        }

        public Task Ping()
        {
            logger.Info("Ping");
            return TaskDone.Done;
        }

        public async Task SendItem(int item)
        {
            if (!State.IsProducer || State.Stream == null) throw new InvalidOperationException("Not a Producer");
            if (logger.IsVerbose) logger.Verbose("SendItem Item={0}", item);
            Exception error = null;
            try
            {
                await State.Stream.OnNextAsync(item);

                if (logger.IsVerbose) logger.Verbose("Successful SendItem " + item);
                State.NumMessagesSent++;
            }
            catch (Exception exc)
            {
                logger.Error(0, "Error from SendItem " + item, exc);
                State.NumErrors++;
                error = exc;
            }
            await WriteStateAsync(); // Update counts in persisted state

            if (error != null)
            {
                throw new AggregateException(error);
            }
            if (logger.IsVerbose) logger.Verbose("Finished SendItem for Item={0}", item);
        }

        public async Task BecomeProducer(Guid streamId, string streamNamespace, string providerName)
        {
            if (logger.IsVerbose) logger.Verbose("BecomeProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            InitStream(streamId, streamNamespace, providerName);
            State.IsProducer = true;

            // Send an initial message to ensure we are properly initialized as a Producer.
            await State.Stream.OnNextAsync(0);
            State.NumMessagesSent++;
            await WriteStateAsync();
            if (logger.IsVerbose) logger.Verbose("Finished BecomeProducer for StreamId={0} StreamProvider={1}", streamId, providerName);
        }

        public async Task ClearGrain()
        {
            logger.Info("ClearGrain");
            State.IsProducer = false;
            State.Stream = null;
            await ClearStateAsync();
        }

        public async Task DoDeactivateNoClose()
        {
            if (logger.IsVerbose) logger.Verbose("DoDeactivateNoClose");

            State.IsProducer = false;
            State.Stream = null;
            await WriteStateAsync();

            if (logger.IsVerbose) logger.Verbose("Calling DeactivateOnIdle");
            DeactivateOnIdle();
        }
    }

    [Serializable]
    public class MyStreamObserver<T> : IAsyncObserver<T>
    {
        internal int NumItems { get; private set; }
        internal int NumErrors { get; private set; }

        private readonly Logger logger;

        internal MyStreamObserver(Logger logger)
        {
            this.logger = logger;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            NumItems++;

            if (logger != null && logger.IsVerbose)
            {
                logger.Verbose("Received OnNextAsync - Item={0} - Total Items={1} Errors={2}", item, NumItems, NumErrors);
            }

            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            if (logger != null)
            {
                logger.Info("Receive OnCompletedAsync - Total Items={0} Errors={1}", NumItems, NumErrors);
            }
            return TaskDone.Done;
        }

        public Task OnErrorAsync(Exception ex)
        {
            NumErrors++;

            if (logger != null)
            {
                logger.Warn(1, "Received OnErrorAsync - Exception={0} - Total Items={1} Errors={2}", ex, NumItems, NumErrors);
            }

            return TaskDone.Done;
        }
    }
}
