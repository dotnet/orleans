using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams.Core;

namespace Orleans.Streams
{
    internal class StreamConsumer<T> : IInternalAsyncObservable<T>
    {
        internal bool                               IsRewindable { get; private set; }

        private readonly StreamImpl<T>              stream;
        private readonly string                     streamProviderName;
        [NonSerialized]
        private readonly IStreamProviderRuntime     providerRuntime;
        [NonSerialized]
        private readonly IStreamPubSub              pubSub;
        private StreamConsumerExtension             myExtension;
        private IStreamConsumerExtension            myGrainReference;
        [NonSerialized]
        private readonly AsyncLock                  bindExtLock;
        [NonSerialized]
        private readonly ILogger logger;

        public StreamConsumer(
            StreamImpl<T> stream,
            string streamProviderName,
            IStreamProviderRuntime runtime,
            IStreamPubSub pubSub,
            ILogger logger,
            bool isRewindable)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            if (pubSub == null) throw new ArgumentNullException(nameof(pubSub));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            this.logger = logger;
            this.stream = stream;
            this.streamProviderName = streamProviderName;
            this.providerRuntime = runtime;
            this.pubSub = pubSub;
            this.IsRewindable = isRewindable;
            this.myExtension = null;
            this.myGrainReference = null;
            this.bindExtLock = new AsyncLock();
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return SubscribeAsyncImpl(observer, null, null);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token,
            string filterData = null)
        {
            return SubscribeAsyncImpl(observer, null, token, filterData);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver)
        {
            return SubscribeAsyncImpl(null, batchObserver, null);
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncBatchObserver<T> batchObserver, StreamSequenceToken token)
        {
            return SubscribeAsyncImpl(null, batchObserver, token);
        }

        private async Task<StreamSubscriptionHandle<T>> SubscribeAsyncImpl(
            IAsyncObserver<T> observer,
            IAsyncBatchObserver<T> batchObserver,
            StreamSequenceToken token,
            string filterData = null)
        {
            if (token != null && !IsRewindable)
                throw new ArgumentNullException(nameof(token), "Passing a non-null token to a non-rewindable IAsyncObservable.");
            if (observer is GrainReference)
                throw new ArgumentException("On-behalf subscription via grain references is not supported. Only passing of object references is allowed.", nameof(observer));
            if (batchObserver is GrainReference)
                throw new ArgumentException("On-behalf subscription via grain references is not supported. Only passing of object references is allowed.", nameof(batchObserver));

            using var _ = RequestContext.SuppressCallChainReentrancy();

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Subscribe Token={Token}", token);
            await BindExtensionLazy();

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Subscribe - Connecting to Rendezvous {PubSub} My GrainRef={GrainReference} Token={Token}",
                pubSub, myGrainReference, token);

            GuidId subscriptionId = pubSub.CreateSubscriptionId(stream.InternalStreamId, myGrainReference.GetGrainId());

            // Optimistic Concurrency: 
            // In general, we should first register the subsription with the pubsub (pubSub.RegisterConsumer)
            // and only if it succeeds store it locally (myExtension.SetObserver). 
            // Basicaly, those 2 operations should be done as one atomic transaction - either both or none and isolated from concurrent reads.
            // BUT: there is a distributed race here: the first msg may arrive before the call is awaited 
            // (since the pubsub notifies the producer that may immideately produce)
            // and will thus not find the subriptionHandle in the extension, basically violating "isolation". 
            // Therefore, we employ Optimistic Concurrency Control here to guarantee isolation: 
            // we optimisticaly store subscriptionId in the handle first before calling pubSub.RegisterConsumer
            // and undo it in the case of failure. 
            // There is no problem with that we call myExtension.SetObserver too early before the handle is registered in pub sub,
            // since this subscriptionId is unique (random Guid) and no one knows it anyway, unless successfully subscribed in the pubsub.
            var subriptionHandle = myExtension.SetObserver(subscriptionId, stream, observer, batchObserver, token, filterData);
            try
            {
                await pubSub.RegisterConsumer(subscriptionId, stream.InternalStreamId, myGrainReference.GetGrainId(), filterData);
                return subriptionHandle;
            }
            catch (Exception)
            {
                // Undo the previous call myExtension.SetObserver.
                myExtension.RemoveObserver(subscriptionId);
                throw;
            }
        }

        public Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null)
        {
            return ResumeAsyncImpl(handle, observer, null, token);
        }

        public Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncBatchObserver<T> batchObserver,
            StreamSequenceToken token = null)
        {
            return ResumeAsyncImpl(handle, null, batchObserver, token);
        }

        private async Task<StreamSubscriptionHandle<T>> ResumeAsyncImpl(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            IAsyncBatchObserver<T> batchObserver,
            StreamSequenceToken token = null)
        {
            using var _ = RequestContext.SuppressCallChainReentrancy();

            StreamSubscriptionHandleImpl<T> oldHandleImpl = CheckHandleValidity(handle);

            if (token != null && !IsRewindable)
                throw new ArgumentNullException(nameof(token), "Passing a non-null token to a non-rewindable IAsyncObservable.");

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Resume Token={Token}", token);
            await BindExtensionLazy();

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Resume - Connecting to Rendezvous {PubSub} My GrainRef={GrainReference} Token={Token}",
                pubSub, myGrainReference, token);

            StreamSubscriptionHandle<T> newHandle = myExtension.SetObserver(oldHandleImpl.SubscriptionId, stream, observer, batchObserver, token, null);

            // On failure caller should be able to retry using the original handle, so invalidate old handle only if everything succeeded.  
            oldHandleImpl.Invalidate();

            return newHandle;
        }

        public async Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle)
        {
            using var _ = RequestContext.SuppressCallChainReentrancy();

            await BindExtensionLazy();

            StreamSubscriptionHandleImpl<T> handleImpl = CheckHandleValidity(handle);

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Unsubscribe StreamSubscriptionHandle={Handle}", handle);

            myExtension.RemoveObserver(handleImpl.SubscriptionId);
            // UnregisterConsumer from pubsub even if does not have this handle locally, to allow UnsubscribeAsync retries.

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Unsubscribe - Disconnecting from Rendezvous {PubSub} My GrainRef={GrainReference}",
                pubSub, myGrainReference);

            await pubSub.UnregisterConsumer(handleImpl.SubscriptionId, stream.InternalStreamId);

            handleImpl.Invalidate();
        }

        public async Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions()
        {
            using var _ = RequestContext.SuppressCallChainReentrancy();

            await BindExtensionLazy();

            List<StreamSubscription> subscriptions= await pubSub.GetAllSubscriptions(stream.InternalStreamId, myGrainReference.GetGrainId());
            return subscriptions.Select(sub => new StreamSubscriptionHandleImpl<T>(GuidId.GetGuidId(sub.SubscriptionId), stream))
                                  .ToList<StreamSubscriptionHandle<T>>();
        }

        public async Task Cleanup()
        {
            using var _ = RequestContext.SuppressCallChainReentrancy();

            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Cleanup() called");
            if (myExtension == null)
                return;

            var allHandles = myExtension.GetAllStreamHandles<T>();
            var tasks = new List<Task>();
            foreach (var handle in allHandles)
            {
                myExtension.RemoveObserver(handle.SubscriptionId);
                tasks.Add(pubSub.UnregisterConsumer(handle.SubscriptionId, stream.InternalStreamId));
            }
            try
            {
                await Task.WhenAll(tasks);

            } catch (Exception exc)
            {
                logger.LogWarning(
                    (int)ErrorCode.StreamProvider_ConsumerFailedToUnregister,
                    exc,
                    "Ignoring unhandled exception during PubSub.UnregisterConsumer");
            }
            myExtension = null;
        }

        // Used in test.
        internal bool InternalRemoveObserver(StreamSubscriptionHandle<T> handle)
        {
            return myExtension != null && myExtension.RemoveObserver(((StreamSubscriptionHandleImpl<T>)handle).SubscriptionId);
        }

        internal Task<int> DiagGetConsumerObserversCount()
        {
            return Task.FromResult(myExtension.DiagCountStreamObservers<T>(stream.InternalStreamId));
        }

        private async Task BindExtensionLazy()
        {
            if (myExtension == null)
            {
                using (await bindExtLock.LockAsync())
                {
                    if (myExtension == null)
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("BindExtensionLazy - Binding local extension to stream runtime={ProviderRuntime}", providerRuntime);
                        (myExtension, myGrainReference) = providerRuntime.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(() => new StreamConsumerExtension(providerRuntime));
                        if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("BindExtensionLazy - Connected Extension={Extension} GrainRef={GrainReference}", myExtension, myGrainReference);                        
                    }
                }
            }
        }

        private StreamSubscriptionHandleImpl<T> CheckHandleValidity(StreamSubscriptionHandle<T> handle)
        {
            if (handle == null)
                throw new ArgumentNullException(nameof(handle));
            if (!handle.StreamId.Equals(stream.StreamId))
                throw new ArgumentException("Handle is not for this stream.", nameof(handle));
            var handleImpl = handle as StreamSubscriptionHandleImpl<T>;
            if (handleImpl == null)
                throw new ArgumentException("Handle type not supported.", nameof(handle));
            if (!handleImpl.IsValid)
                throw new ArgumentException("Handle is no longer valid.  It has been used to unsubscribe or resume.", nameof(handle));
            return handleImpl;
        }
    }
}
