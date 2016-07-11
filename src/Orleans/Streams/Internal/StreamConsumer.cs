using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;

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
        private readonly Logger logger;

        public StreamConsumer(StreamImpl<T> stream, string streamProviderName, IStreamProviderRuntime providerUtilities, IStreamPubSub pubSub, bool isRewindable)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (providerUtilities == null) throw new ArgumentNullException("providerUtilities");
            if (pubSub == null) throw new ArgumentNullException("pubSub");

            logger = LogManager.GetLogger(string.Format("StreamConsumer<{0}>-{1}", typeof(T).Name, stream), LoggerType.Runtime);
            this.stream = stream;
            this.streamProviderName = streamProviderName;
            providerRuntime = providerUtilities;
            this.pubSub = pubSub;
            IsRewindable = isRewindable;
            myExtension = null;
            myGrainReference = null;
            bindExtLock = new AsyncLock();
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return SubscribeAsync(observer, null);
        }

        public async Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token,
            StreamFilterPredicate filterFunc = null,
            object filterData = null)
        {
            if (token != null && !IsRewindable)
                throw new ArgumentNullException("token", "Passing a non-null token to a non-rewindable IAsyncObservable.");
            if (observer is GrainReference)
                throw new ArgumentException("On-behalf subscription via grain references is not supported. Only passing of object references is allowed.", "observer");

            if (logger.IsVerbose) logger.Verbose("Subscribe Observer={0} Token={1}", observer, token);
            await BindExtensionLazy();

            IStreamFilterPredicateWrapper filterWrapper = null;
            if (filterFunc != null)
                filterWrapper = new FilterPredicateWrapperData(filterData, filterFunc);
            
            if (logger.IsVerbose) logger.Verbose("Subscribe - Connecting to Rendezvous {0} My GrainRef={1} Token={2}",
                pubSub, myGrainReference, token);

            GuidId subscriptionId = pubSub.CreateSubscriptionId(stream.StreamId, myGrainReference);

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
            var subriptionHandle = myExtension.SetObserver(subscriptionId, stream, observer, token, filterWrapper);
            try
            {
                await pubSub.RegisterConsumer(subscriptionId, stream.StreamId, streamProviderName, myGrainReference, filterWrapper);
                return subriptionHandle;
            } catch(Exception)
            {
                // Undo the previous call myExtension.SetObserver.
                myExtension.RemoveObserver(subscriptionId);
                throw;
            }            
        }

        public async Task<StreamSubscriptionHandle<T>> ResumeAsync(
            StreamSubscriptionHandle<T> handle,
            IAsyncObserver<T> observer,
            StreamSequenceToken token = null)
        {
            StreamSubscriptionHandleImpl<T> oldHandleImpl = CheckHandleValidity(handle);

            if (token != null && !IsRewindable)
                throw new ArgumentNullException("token", "Passing a non-null token to a non-rewindable IAsyncObservable.");

            if (logger.IsVerbose) logger.Verbose("Resume Observer={0} Token={1}", observer, token);
            await BindExtensionLazy();

            if (logger.IsVerbose) logger.Verbose("Resume - Connecting to Rendezvous {0} My GrainRef={1} Token={2}",
                pubSub, myGrainReference, token);

            StreamSubscriptionHandle<T> newHandle = myExtension.SetObserver(oldHandleImpl.SubscriptionId, stream, observer, token, null);

            // On failure caller should be able to retry using the original handle, so invalidate old handle only if everything succeeded.  
            oldHandleImpl.Invalidate();

            return newHandle;
        }

        public async Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle)
        {
            await BindExtensionLazy();

            StreamSubscriptionHandleImpl<T> handleImpl = CheckHandleValidity(handle);

            if (logger.IsVerbose) logger.Verbose("Unsubscribe StreamSubscriptionHandle={0}", handle);

            myExtension.RemoveObserver(handleImpl.SubscriptionId);
            // UnregisterConsumer from pubsub even if does not have this handle localy, to allow UnsubscribeAsync retries.

            if (logger.IsVerbose) logger.Verbose("Unsubscribe - Disconnecting from Rendezvous {0} My GrainRef={1}",
                pubSub, myGrainReference);

            await pubSub.UnregisterConsumer(handleImpl.SubscriptionId, stream.StreamId, streamProviderName);

            handleImpl.Invalidate();
        }

        public async Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions()
        {
            await BindExtensionLazy();

            List<GuidId> subscriptionIds = await pubSub.GetAllSubscriptions(stream.StreamId, myGrainReference);
            return subscriptionIds.Select(id => new StreamSubscriptionHandleImpl<T>(id, stream, IsRewindable))
                                  .ToList<StreamSubscriptionHandle<T>>();
        }

        public async Task Cleanup()
        {
            if (logger.IsVerbose) logger.Verbose("Cleanup() called");
            if (myExtension == null)
                return;

            var allHandles = myExtension.GetAllStreamHandles<T>();
            var tasks = new List<Task>();
            foreach (var handle in allHandles)
            {
                myExtension.RemoveObserver(handle.SubscriptionId);
                tasks.Add(pubSub.UnregisterConsumer(handle.SubscriptionId, stream.StreamId, streamProviderName));
            }
            try
            {
                await Task.WhenAll(tasks);

            } catch (Exception exc)
            {
                logger.Warn(ErrorCode.StreamProvider_ConsumerFailedToUnregister,
                    "Ignoring unhandled exception during PubSub.UnregisterConsumer", exc);
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
            return Task.FromResult(myExtension.DiagCountStreamObservers<T>(stream.StreamId));
        }

        private async Task BindExtensionLazy()
        {
            if (myExtension == null)
            {
                using (await bindExtLock.LockAsync())
                {
                    if (myExtension == null)
                    {
                        if (logger.IsVerbose) logger.Verbose("BindExtensionLazy - Binding local extension to stream runtime={0}", providerRuntime);
                        var tup = await providerRuntime.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                            () => new StreamConsumerExtension(providerRuntime, IsRewindable));
                        myExtension = tup.Item1;
                        myGrainReference = tup.Item2;
                        if (logger.IsVerbose) logger.Verbose("BindExtensionLazy - Connected Extension={0} GrainRef={1}", myExtension, myGrainReference);                        
                    }
                }
            }
        }

        private StreamSubscriptionHandleImpl<T> CheckHandleValidity(StreamSubscriptionHandle<T> handle)
        {
            if (handle == null)
                throw new ArgumentNullException("handle");
            if (!handle.StreamIdentity.Equals(stream))
                throw new ArgumentException("Handle is not for this stream.", "handle");
            var handleImpl = handle as StreamSubscriptionHandleImpl<T>;
            if (handleImpl == null)
                throw new ArgumentException("Handle type not supported.", "handle");
            if (!handleImpl.IsValid)
                throw new ArgumentException("Handle is no longer valid.  It has been used to unsubscribe or resume.", "handle");
            return handleImpl;
        }
    }
}
