#define COUNT_ACTIVATE_DEACTIVATE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    [GenerateSerializer]
    public class StreamLifecycleTestGrainState
    {
        // For producer and consumer
        // -- only need to store this because of how we run our unit tests against multiple providers
        [Id(0)]
        public string StreamProviderName { get; set; }

        // For producer only.
        [Id(1)]
        public IAsyncStream<int> Stream { get; set; }
        [Id(2)]
        public bool IsProducer { get; set; }
        [Id(3)]
        public int NumMessagesSent { get; set; }
        [Id(4)]
        public int NumErrors { get; set; }

        // For consumer only.
        [Id(5)]
        public HashSet<StreamSubscriptionHandle<int>> ConsumerSubscriptionHandles { get; set; }

        public StreamLifecycleTestGrainState()
        {
            ConsumerSubscriptionHandles = new HashSet<StreamSubscriptionHandle<int>>();
        }
    }

    public class GenericArg
    {
        public string A { get; private set; }
        public int B { get; private set; }

        public GenericArg(string a, int b)
        {
            A = a;
            B = b;
        }

        public override bool Equals(object obj)
        {
            var item = obj as GenericArg;
            if (item == null)
            {
                return false;
            }

            return A.Equals(item.A) && B.Equals(item.B);
        }

        public override int GetHashCode()
        {
            return (B * 397) ^ (A != null ? A.GetHashCode() : 0);
        }
    }

    public class AsyncObserverArg : GenericArg
    {
        public AsyncObserverArg(string a, int b) : base(a, b) { }
    }

    public class AsyncObservableArg : GenericArg
    {
        public AsyncObservableArg(string a, int b) : base(a, b) { }
    }

    public class AsyncStreamArg : GenericArg
    {
        public AsyncStreamArg(string a, int b) : base(a, b) { }
    }

    public class StreamSubscriptionHandleArg : GenericArg
    {
        public StreamSubscriptionHandleArg(string a, int b) : base(a, b) { }
    }

    public class StreamLifecycleTestGrainBase : Grain<StreamLifecycleTestGrainState>
    {
        protected ILogger logger;
        protected string _lastProviderName;
        protected IStreamProvider _streamProvider;

#if COUNT_ACTIVATE_DEACTIVATE
        private IActivateDeactivateWatcherGrain watcher;
#endif

        public StreamLifecycleTestGrainBase(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        protected Task RecordActivate()
        {
#if COUNT_ACTIVATE_DEACTIVATE
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            return watcher.RecordActivateCall(IdentityString);
#else
            return Task.CompletedTask;
#endif
        }

        protected Task RecordDeactivate()
        {
#if COUNT_ACTIVATE_DEACTIVATE
            return watcher.RecordDeactivateCall(IdentityString);
#else
            return Task.CompletedTask;
#endif
        }

        protected void InitStream(StreamId streamId, string providerToUse)
        {
            if (providerToUse == null) throw new ArgumentNullException("providerToUse", "Can't have null stream provider name");

            if (State.Stream != null && !State.Stream.StreamId.Equals(streamId))
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Stream already exists for StreamId={0} StreamProvider={1} - Resetting", State.Stream, providerToUse);

                // Note: in this test, we are deliberately not doing Unsubscribe consumers, just discard old stream and let auto-cleanup functions do their thing.
                State.ConsumerSubscriptionHandles.Clear();
                State.IsProducer = false;
                State.NumMessagesSent = 0;
                State.NumErrors = 0;
                State.Stream = null;
            }

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("InitStream StreamId={0} StreamProvider={1}", streamId, providerToUse);

            if (providerToUse != _lastProviderName)
            {
                _streamProvider = this.GetStreamProvider(providerToUse);
                _lastProviderName = providerToUse;
            }
            IAsyncStream<int> stream = _streamProvider.GetStream<int>(streamId);
            State.Stream = stream;
            State.StreamProviderName = providerToUse;

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("InitStream returning with Stream={0} with ref type = {1}", State.Stream, State.Stream.GetType().FullName);
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    internal class StreamLifecycleConsumerGrain : StreamLifecycleTestGrainBase, IStreamLifecycleConsumerGrain
    {
        protected readonly InsideRuntimeClient runtimeClient;
        protected readonly IStreamProviderRuntime streamProviderRuntime;

        public StreamLifecycleConsumerGrain(InsideRuntimeClient runtimeClient, IStreamProviderRuntime streamProviderRuntime, ILoggerFactory loggerFactory) : base(loggerFactory)
        {
            this.runtimeClient = runtimeClient;
            this.streamProviderRuntime = streamProviderRuntime;
        }

        protected IDictionary<StreamSubscriptionHandle<int>, MyStreamObserver<int>> Observers { get; set; }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("OnActivateAsync");

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
                        var observer = new MyStreamObserver<int>(this.logger);
                        StreamSubscriptionHandle<int> subsHandle = await handle.ResumeAsync(observer);
                        Observers.Add(subsHandle, observer);
                    }
                }
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Not conected to stream yet.");
            }
        }
        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("OnDeactivateAsync");
            await RecordDeactivate();
        }

        public Task<int> GetReceivedCount()
        {
            int numReceived = Observers.Sum(o => o.Value.NumItems);
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("ReceivedCount={0}", numReceived);
            return Task.FromResult(numReceived);
        }
        public Task<int> GetErrorsCount()
        {
            int numErrors = Observers.Sum(o => o.Value.NumErrors);
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("ErrorsCount={0}", numErrors);
            return Task.FromResult(numErrors);
        }

        public Task Ping()
        {
            logger.Info("Ping");
            return Task.CompletedTask;
        }

        public virtual async Task BecomeConsumer(StreamId streamId, string providerToUse)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("BecomeConsumer StreamId={0} StreamProvider={1} Grain={2}", streamId, providerToUse, this.AsReference<IStreamLifecycleConsumerGrain>());
            InitStream(streamId, providerToUse);
            var observer = new MyStreamObserver<int>(logger);
            var subsHandle = await State.Stream.SubscribeAsync(observer);
            State.ConsumerSubscriptionHandles.Add(subsHandle);
            Observers.Add(subsHandle, observer);
            await WriteStateAsync();
        }

        public virtual async Task TestBecomeConsumerSlim(StreamId streamId, string providerName)
        {
            InitStream(streamId, providerName);
            var observer = new MyStreamObserver<int>(logger);

            //var subsHandle = await State.Stream.SubscribeAsync(observer);

            var (myExtension, myExtensionReference) = this.streamProviderRuntime.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                () => new StreamConsumerExtension(streamProviderRuntime));
            string extKey = providerName + "_" + Encoding.UTF8.GetString(State.Stream.StreamId.Namespace.ToArray());
            var id = new InternalStreamId(providerName, streamId);
            IPubSubRendezvousGrain pubsub = GrainFactory.GetGrain<IPubSubRendezvousGrain>(id.ToString());
            GuidId subscriptionId = GuidId.GetNewGuidId();
            await pubsub.RegisterConsumer(subscriptionId, ((StreamImpl<int>)State.Stream).InternalStreamId, myExtensionReference, null);

            myExtension.SetObserver(subscriptionId, ((StreamImpl<int>)State.Stream), observer, null, null, null);
        }

        public async Task RemoveConsumer(StreamId streamId, string providerName, StreamSubscriptionHandle<int> subsHandle)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("RemoveConsumer StreamId={0} StreamProvider={1}", streamId, providerName);
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
    public class StreamLifecycleProducerGrain : StreamLifecycleTestGrainBase, IStreamLifecycleProducerGrain
    {
        public StreamLifecycleProducerGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("OnActivateAsync");

            await RecordActivate();

            if (State.Stream != null && State.StreamProviderName != null)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Reconnected to stream {0}", State.Stream);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Not connected to stream yet.");
            }
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("OnDeactivateAsync");
            await RecordDeactivate();
        }

        public Task<int> GetSendCount()
        {
            int result = State.NumMessagesSent;
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("GetSendCount={0}", result);
            return Task.FromResult(result);
        }

        public Task<int> GetErrorsCount()
        {
            int result = State.NumErrors;
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("GetErrorsCount={0}", result);
            return Task.FromResult(result);
        }

        public Task Ping()
        {
            logger.Info("Ping");
            return Task.CompletedTask;
        }

        public async Task SendItem(int item)
        {
            if (!State.IsProducer || State.Stream == null) throw new InvalidOperationException("Not a Producer");
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("SendItem Item={0}", item);
            Exception error = null;
            try
            {
                await State.Stream.OnNextAsync(item);

                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Successful SendItem " + item);
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
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Finished SendItem for Item={0}", item);
        }

        public async Task BecomeProducer(StreamId streamId, string providerName)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("BecomeProducer StreamId={0} StreamProvider={1}", streamId, providerName);
            InitStream(streamId, providerName);
            State.IsProducer = true;

            // Send an initial message to ensure we are properly initialized as a Producer.
            await State.Stream.OnNextAsync(0);
            State.NumMessagesSent++;
            await WriteStateAsync();
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Finished BecomeProducer for StreamId={0} StreamProvider={1}", streamId, providerName);
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
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("DoDeactivateNoClose");

            State.IsProducer = false;
            State.Stream = null;
            await WriteStateAsync();

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Calling DeactivateOnIdle");
            DeactivateOnIdle();
        }
    }

    [Serializable]
    [GenerateSerializer]
    public class MyStreamObserver<T> : IAsyncObserver<T>
    {
        [Id(0)]
        internal int NumItems { get; private set; }
        [Id(1)]
        internal int NumErrors { get; private set; }

        [Id(2)]
        private readonly ILogger logger;

        internal MyStreamObserver(ILogger logger)
        {
            this.logger = logger;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token)
        {
            NumItems++;

            if (logger != null && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Received OnNextAsync - Item={0} - Total Items={1} Errors={2}", item, NumItems, NumErrors);
            }

            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            if (logger != null)
            {
                logger.Info("Receive OnCompletedAsync - Total Items={0} Errors={1}", NumItems, NumErrors);
            }
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            NumErrors++;

            if (logger != null)
            {
                logger.Warn(1, "Received OnErrorAsync - Exception={0} - Total Items={1} Errors={2}", ex, NumItems, NumErrors);
            }

            return Task.CompletedTask;
        }
    }

    public class ClosedTypeStreamObserver : MyStreamObserver<AsyncObserverArg>
    {
        public ClosedTypeStreamObserver(ILogger logger) : base(logger)
        {
        }
    }

    public interface IClosedTypeAsyncObservable : IAsyncObservable<AsyncObservableArg> { }

    public interface IClosedTypeAsyncStream : IAsyncStream<AsyncStreamArg> { }

    internal class ClosedTypeStreamSubscriptionHandle : StreamSubscriptionHandleImpl<StreamSubscriptionHandleArg>
    {
        public ClosedTypeStreamSubscriptionHandle() : base(null, null) { /* not a subject to the creation */ }
    }
}
