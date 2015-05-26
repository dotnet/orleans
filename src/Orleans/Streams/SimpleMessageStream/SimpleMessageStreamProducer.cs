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
using System.Threading.Tasks;

using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    internal class SimpleMessageStreamProducer<T> : IAsyncBatchObserver<T>, IStreamControl
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
        [NonSerialized]
        private bool                                    isDisposed;
        [NonSerialized]
        private readonly Logger                         logger;
        [NonSerialized]
        private readonly AsyncLock                      initLock;

        internal bool IsRewindable { get; private set; }

        internal SimpleMessageStreamProducer(StreamImpl<T> stream, string streamProviderName, IStreamProviderRuntime providerUtilities, bool fireAndForgetDelivery, bool isRewindable)
        {
            this.stream = stream;
            this.streamProviderName = streamProviderName;
            providerRuntime = providerUtilities;
            pubSub = providerRuntime.PubSub(StreamPubSubType.GrainBased);
            connectedToRendezvous = false;
            this.fireAndForgetDelivery = fireAndForgetDelivery;
            IsRewindable = isRewindable;
            isDisposed = false;
            logger = providerRuntime.GetLogger(GetType().Name);
            initLock = new AsyncLock();

            ConnectToRendezvous().Ignore();
        }

        private async Task<ISet<PubSubSubscriptionState>> RegisterProducer()
        {
            var tup = await providerRuntime.BindExtension<SimpleMessageStreamProducerExtension, IStreamProducerExtension>(
                () => new SimpleMessageStreamProducerExtension(providerRuntime, fireAndForgetDelivery));

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
                await ConnectToRendezvous();

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
