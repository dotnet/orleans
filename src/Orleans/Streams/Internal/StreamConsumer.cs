/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

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
        private readonly TraceLogger                logger;

        public StreamConsumer(StreamImpl<T> stream, string streamProviderName, IStreamProviderRuntime providerUtilities, IStreamPubSub pubSub, bool isRewindable)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (providerUtilities == null) throw new ArgumentNullException("providerUtilities");
            if (pubSub == null) throw new ArgumentNullException("pubSub");

            logger = TraceLogger.GetLogger(string.Format("StreamConsumer<{0}>-{1}", typeof(T).Name, stream), TraceLogger.LoggerType.Runtime);
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
            
            if (logger.IsVerbose) logger.Verbose("Subscribe Observer={0} Token={1}", observer, token);
            await BindExtensionLazy();

            IStreamFilterPredicateWrapper filterWrapper = null;
            if (filterFunc != null)
                filterWrapper = new FilterPredicateWrapperData(filterData, filterFunc);
            
            if (logger.IsVerbose) logger.Verbose("Subscribe - Connecting to Rendezvous {0} My GrainRef={1} Token={2}",
                pubSub, myGrainReference, token);

            GuidId subscriptionId = pubSub.CreateSubscriptionId(myGrainReference, stream.StreamId);
            await pubSub.RegisterConsumer(subscriptionId, stream.StreamId, streamProviderName, myGrainReference, filterWrapper);

            return myExtension.SetObserver(subscriptionId, stream, observer, token, filterWrapper);
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
            bool shouldUnsubscribe = myExtension.RemoveObserver(handle);
            if (!shouldUnsubscribe) return;

            if (logger.IsVerbose) logger.Verbose("Unsubscribe - Disconnecting from Rendezvous {0} My GrainRef={1}",
                pubSub, myGrainReference);

            await pubSub.UnregisterConsumer(handleImpl.SubscriptionId, stream.StreamId, streamProviderName);

            handleImpl.Invalidate();
        }

        public async Task<IList<StreamSubscriptionHandle<T>>> GetAllSubscriptions()
        {
            await BindExtensionLazy();

            List<GuidId> subscriptionIds = await pubSub.GetAllSubscriptions(stream.StreamId, myGrainReference);
            return subscriptionIds.Select(id => new StreamSubscriptionHandleImpl<T>(id, stream))
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
                myExtension.RemoveObserver(handle);
                tasks.Add(pubSub.UnregisterConsumer(handle.SubscriptionId, stream.StreamId, streamProviderName));
            }
            try
            {
                await Task.WhenAll(tasks);

            } catch (Exception exc)
            {
                logger.Warn((int)ErrorCode.StreamProvider_ConsumerFailedToUnregister,
                    "Ignoring unhandled exception during PubSub.UnregisterConsumer", exc);
            }
            myExtension = null;
        }

        // Used in test.
        internal bool InternalRemoveObserver(StreamSubscriptionHandle<T> handle)
        {
            return myExtension != null && myExtension.RemoveObserver(handle);
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
                            () => new StreamConsumerExtension(providerRuntime));
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
