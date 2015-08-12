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
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.SimpleMessageStream
{
    public class SimpleMessageStreamProvider : IInternalStreamProvider
    {
        public string                       Name { get; private set; }

        private Logger                      logger;
        private IStreamProviderRuntime      providerRuntime;
        private bool                        fireAndForgetDelivery;
        internal const string               FIRE_AND_FORGET_DELIVERY = "FireAndForgetDelivery";
        internal const bool                 DEFAULT_FIRE_AND_FORGET_DELIVERY_VALUE = false;

        public bool IsRewindable { get { return false; } }

        public Task Init(string name, IProviderRuntime providerUtilitiesManager, IProviderConfiguration config)
        {
            this.Name = name;
            providerRuntime = (IStreamProviderRuntime) providerUtilitiesManager;
            string fireAndForgetDeliveryStr;
            fireAndForgetDelivery = !config.Properties.TryGetValue(FIRE_AND_FORGET_DELIVERY, out fireAndForgetDeliveryStr) || Boolean.Parse(fireAndForgetDeliveryStr);

            logger = providerRuntime.GetLogger(this.GetType().Name);
            logger.Info("Initialized SimpleMessageStreamProvider with name {0} and with property FireAndForgetDelivery: {1}.", Name, fireAndForgetDelivery);
            return TaskDone.Done;
        }

        public Task Start()
        {
            return TaskDone.Done;
        }

        public IAsyncStream<T> GetStream<T>(Guid id, string streamNamespace)
        {
            var streamId = StreamId.GetStreamId(id, Name, streamNamespace);
            return providerRuntime.GetStreamDirectory().GetOrAddStream<T>(
                streamId,
                () => new StreamImpl<T>(streamId, this, IsRewindable));
        }

        IInternalAsyncBatchObserver<T> IInternalStreamProvider.GetProducerInterface<T>(IAsyncStream<T> stream)
        {
            return new SimpleMessageStreamProducer<T>((StreamImpl<T>)stream, Name, providerRuntime, fireAndForgetDelivery, IsRewindable);
        }

        IInternalAsyncObservable<T> IInternalStreamProvider.GetConsumerInterface<T>(IAsyncStream<T> streamId)
        {
            return GetConsumerInterfaceImpl(streamId);
        }

        private IInternalAsyncObservable<T> GetConsumerInterfaceImpl<T>(IAsyncStream<T> stream)
        {
            return new StreamConsumer<T>((StreamImpl<T>)stream, Name, providerRuntime, providerRuntime.PubSub(StreamPubSubType.GrainBased), IsRewindable);
        }
    }
}
