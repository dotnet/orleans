using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;
using Microsoft.Extensions.Logging;
using Orleans.Streams.Filtering;
using Orleans.Serialization;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    internal class SimpleMessageStreamProducer<T> : IInternalAsyncBatchObserver<T>
    {
        private readonly StreamImpl<T>                  stream;
        private readonly string                         streamProviderName;

        [NonSerialized]
        private readonly DeepCopier<T> deepCopier;

        [NonSerialized]
        private readonly IStreamPubSub                  pubSub;
        private readonly IStreamFilter streamFilter;
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
        private readonly ILogger                         logger;
        [NonSerialized]
        private readonly AsyncLock                      initLock;
        internal bool IsRewindable { get; private set; }

        internal SimpleMessageStreamProducer(
            StreamImpl<T> stream,
            string streamProviderName,
            IStreamProviderRuntime providerUtilities,
            bool fireAndForgetDelivery,
            bool optimizeForImmutableData,
            IStreamPubSub pubSub,
            IStreamFilter streamFilter,
            bool isRewindable,
            DeepCopier<T> deepCopier,
            ILogger<SimpleMessageStreamProducer<T>> logger)
        {
            this.stream = stream;
            this.streamProviderName = streamProviderName;
            providerRuntime = providerUtilities;
            this.pubSub = pubSub;
            this.streamFilter = streamFilter;
            this.deepCopier = deepCopier;
            connectedToRendezvous = false;
            this.fireAndForgetDelivery = fireAndForgetDelivery;
            this.optimizeForImmutableData = optimizeForImmutableData;
            IsRewindable = isRewindable;
            isDisposed = false;
            initLock = new AsyncLock();
            this.logger = logger;
            ConnectToRendezvous().Ignore();
        }

        private async Task<ISet<PubSubSubscriptionState>> RegisterProducer()
        {
            (myExtension, myGrainReference) = providerRuntime.BindExtension<SimpleMessageStreamProducerExtension, IStreamProducerExtension>(
                () => new SimpleMessageStreamProducerExtension(providerRuntime, pubSub, this.streamFilter, this.logger, fireAndForgetDelivery, optimizeForImmutableData));

            myExtension.AddStream(stream.InternalStreamId);

            // Notify streamRendezvous about new stream streamProducer. Retreave the list of RemoteSubscribers.
            return await pubSub.RegisterProducer(stream.InternalStreamId, myGrainReference);
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
                    myExtension.AddSubscribers(stream.InternalStreamId, remoteSubscribers);
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
                    item = this.deepCopier.Copy(item);
                }

                await ConnectToRendezvous();
            }

            await myExtension.DeliverItem(stream.InternalStreamId, item);
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

            await myExtension.CompleteStream(stream.InternalStreamId);
        }

        public async Task OnErrorAsync(Exception exc)
        {
            if (isDisposed) throw new ObjectDisposedException(string.Format("{0}-{1}", GetType(), "OnErrorAsync"));

            if (!connectedToRendezvous)
                await ConnectToRendezvous();

            await myExtension.ErrorInStream(stream.InternalStreamId, exc);
        }

        internal Action OnDisposeTestHook { get; set; }

        public async Task Cleanup()
        {
            if(logger.IsEnabled(LogLevel.Debug)) logger.Debug("Cleanup() called");

            myExtension?.RemoveStream(stream.InternalStreamId);

            if (isDisposed) return;

            if (connectedToRendezvous)
            {
                try
                {
                    await pubSub.UnregisterProducer(stream.InternalStreamId, myGrainReference);
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
