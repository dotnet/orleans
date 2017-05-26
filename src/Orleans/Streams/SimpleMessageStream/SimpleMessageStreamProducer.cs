using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    internal class SimpleMessageStreamProducer<T> : IInternalAsyncBatchObserver<T>
    {
        private readonly StreamImpl<T>                  stream;
        private readonly string                         streamProviderName;


        [NonSerialized]
        private readonly IStreamPubSub                  pubSub;

        [NonSerialized]
        private readonly IStreamProviderRuntime         providerRuntime;
        private SimpleMessageStreamProducerExtension    myExtension;
        private IStreamProducerExtension                myGrainReference;
        private bool                                    connectedToRendezvous;
        private readonly bool                           fireAndForgetDelivery;
        private readonly bool                           optimizeForImmutableData;
        [NonSerialized]
        private bool                                    isDisposed;
        [NonSerialized]
        private readonly Logger                         logger;
        [NonSerialized]
        private readonly AsyncLock                      initLock;

        internal bool IsRewindable { get; private set; }

        internal SimpleMessageStreamProducer(StreamImpl<T> stream, string streamProviderName,
            IStreamProviderRuntime providerUtilities, bool fireAndForgetDelivery, bool optimizeForImmutableData,
            IStreamPubSub pubSub, bool isRewindable)
        {
            this.stream = stream;
            this.streamProviderName = streamProviderName;
            providerRuntime = providerUtilities;
            this.pubSub = pubSub;
            connectedToRendezvous = false;
            this.fireAndForgetDelivery = fireAndForgetDelivery;
            this.optimizeForImmutableData = optimizeForImmutableData;
            IsRewindable = isRewindable;
            isDisposed = false;
            logger = providerRuntime.GetLogger(GetType().Name);
            initLock = new AsyncLock();

            ConnectToRendezvous().Ignore();
        }

        private async Task<ISet<PubSubSubscriptionState>> RegisterProducer()
        {
            var tup = await providerRuntime.BindExtension<SimpleMessageStreamProducerExtension, IStreamProducerExtension>(
                () => new SimpleMessageStreamProducerExtension(providerRuntime, pubSub, fireAndForgetDelivery, optimizeForImmutableData));

            myExtension = tup.Item1;
            myGrainReference = tup.Item2;

            myExtension.AddStream(stream.StreamId);

            // Notify streamRendezvous about new stream streamProducer. Retreave the list of RemoteSubscribers.
            return await pubSub.RegisterProducer(stream.StreamId, streamProviderName, myGrainReference);
        }

        private async Task ConnectToRendezvous()
        {
            if (isDisposed)
                throw new ObjectDisposedException(string.Format("{0}-{1}", GetType(), "ConnectToRendezvous"));

            // the caller should check _connectedToRendezvous before calling this method.
            using (await initLock.LockAsync())
            {
                if (!connectedToRendezvous) // need to re-check again.
                {
                    var remoteSubscribers = await RegisterProducer();
                    myExtension.AddSubscribers(stream.StreamId, remoteSubscribers);
                    connectedToRendezvous = true;
                }
            }
        }

        public async Task OnNextAsync(T item, StreamSequenceToken token)
        {
            if (token != null && !IsRewindable)
                throw new ArgumentNullException("token", "Passing a non-null token to a non-rewindable IAsyncBatchObserver.");
            

            if (isDisposed) throw new ObjectDisposedException(string.Format("{0}-{1}", GetType(), "OnNextAsync"));

            if (!connectedToRendezvous)
            {
                if (!this.optimizeForImmutableData)
                {
                    // In order to avoid potential concurrency errors, synchronously copy the input before yielding the
                    // thread. DeliverItem below must also be take care to avoid yielding before copying for non-immutable objects.
                    item = (T) SerializationManager.DeepCopy(item);
                }

                await ConnectToRendezvous();
            }

            await myExtension.DeliverItem(stream.StreamId, item);
        }

        public Task OnNextBatchAsync(IEnumerable<T> batch, StreamSequenceToken token)
        {
            if (token != null && !IsRewindable) throw new ArgumentNullException("token", "Passing a non-null token to a non-rewindable IAsyncBatchObserver.");
            
            throw new NotImplementedException("We still don't support OnNextBatchAsync()");
        }

        public async Task OnCompletedAsync()
        {
            if (isDisposed) throw new ObjectDisposedException(string.Format("{0}-{1}", GetType(), "OnCompletedAsync"));

            if (!connectedToRendezvous)
                await ConnectToRendezvous();

            await myExtension.CompleteStream(stream.StreamId);
        }

        public async Task OnErrorAsync(Exception exc)
        {
            if (isDisposed) throw new ObjectDisposedException(string.Format("{0}-{1}", GetType(), "OnErrorAsync"));

            if (!connectedToRendezvous)
                await ConnectToRendezvous();

            await myExtension.ErrorInStream(stream.StreamId, exc);
        }

        internal Action OnDisposeTestHook { get; set; }

        public async Task Cleanup()
        {
            if(logger.IsVerbose) logger.Verbose("Cleanup() called");

            myExtension.RemoveStream(stream.StreamId);

            if (isDisposed) return;

            if (connectedToRendezvous)
            {
                try
                {
                    await pubSub.UnregisterProducer(stream.StreamId, streamProviderName, myGrainReference);
                    connectedToRendezvous = false;
                }
                catch (Exception exc)
                {
                    logger.Warn((int) ErrorCode.StreamProvider_ProducerFailedToUnregister,
                        "Ignoring unhandled exception during PubSub.UnregisterProducer", exc);
                }
            }
            isDisposed = true;

            Action onDisposeTestHook = OnDisposeTestHook; // capture
            if (onDisposeTestHook != null)
                onDisposeTestHook();
        }
    }
}
