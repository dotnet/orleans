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

﻿using System;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Streams
{
    internal class StreamConsumer<T> : IAsyncObservable<T>
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
        private bool                                connectedToRendezvous;
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
            connectedToRendezvous = false;
            bindExtLock = new AsyncLock();
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            return SubscribeAsync(observer, null, null, null);
        }

        public async Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token,
            StreamFilterPredicate filterFunc, object filterData)
        {
            if (token != null && !IsRewindable)
                throw new ArgumentNullException("token", "Passing a non-null token to a non-rewindable IAsyncObservable.");
            
            if (logger.IsVerbose) logger.Verbose("Subscribe Observer={0} Token={1}", observer, token);
            await BindExtensionLazy();

            IStreamFilterPredicateWrapper filterWrapper = null;
            if (filterFunc != null)
                filterWrapper = new FilterPredicateWrapperData(filterData, filterFunc);
            
            if (!connectedToRendezvous)
            {
                if (logger.IsVerbose) logger.Verbose("Subscribe - Connecting to Rendezvous {0} My GrainRef={1} Token={2}",
                    pubSub, myGrainReference, token);

                await pubSub.RegisterConsumer(stream.StreamId, streamProviderName, myGrainReference, token, filterWrapper);
                connectedToRendezvous = true;
            }
            else if (filterWrapper != null)
            {
                // Already connected and registered this grain, but also need to register this additional filter too. 
                await pubSub.RegisterConsumer(stream.StreamId, streamProviderName, myGrainReference, token, filterWrapper);
            }
                
            return myExtension.AddObserver(stream, observer, filterWrapper);
        }

        public async Task UnsubscribeAsync(StreamSubscriptionHandle<T> handle)
        {
            await BindExtensionLazy();

            if (logger.IsVerbose) logger.Verbose("Unsubscribe StreamSubscriptionHandle={0}", handle);
            bool shouldUnsubscribe = myExtension.RemoveObserver(handle);
            if (!shouldUnsubscribe) return;

            try
            {
                if (logger.IsVerbose) logger.Verbose("Unsubscribe - Disconnecting from Rendezvous {0} My GrainRef={1}",
                    pubSub, myGrainReference);

                await pubSub.UnregisterConsumer(stream.StreamId, streamProviderName, myGrainReference);
            }
            finally
            {
                connectedToRendezvous = false;
            }
        }

        public Task UnsubscribeAllAsync()
        {
            throw new NotImplementedException("UnsubscribeAllAsync not implemented yet.");
        }

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
    }
}