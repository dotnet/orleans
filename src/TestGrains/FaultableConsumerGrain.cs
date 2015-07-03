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
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class FaultableConsumerGrain : Grain, IFaultableConsumerGrain
    {
        private IAsyncObservable<int> consumer;
        private int eventsConsumedCount;
        private int errorsCount;
        private int eventsFailedCount;
        private Logger logger;
        private StreamSubscriptionHandle<int> consumerHandle;
        private Stopwatch failPeriodTimer;
        private TimeSpan failPeriod;

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("FaultableConsumerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            eventsConsumedCount = 0;
            errorsCount = 0;
            eventsFailedCount = 0;
            consumerHandle = null;
            failPeriodTimer = null;
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return TaskDone.Done;
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.Info("BecomeConsumer");
            IStreamProvider streamProvider = GetStreamProvider(providerToUse);
            consumer = streamProvider.GetStream<int>(streamId, streamNamespace);
            consumerHandle = await consumer.SubscribeAsync(OnNextAsync, OnErrorAsync, OnActivateAsync);
        }

        public Task SetFailPeriod(TimeSpan failurePeriod)
        {
            failPeriod = failurePeriod;
            failPeriodTimer = Stopwatch.StartNew();
            return TaskDone.Done;
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            if (consumerHandle != null)
            {
                await consumerHandle.UnsubscribeAsync();
                consumerHandle = null;
            }
        }

        public Task<int> GetNumberConsumed()
        {
            return Task.FromResult(eventsConsumedCount);
        }

        public Task<int> GetNumberFailed()
        {
            return Task.FromResult(eventsFailedCount);
        }

        public Task<int> GetErrorCount()
        {
            return Task.FromResult(errorsCount);
        }

        public Task OnNextAsync(int item, StreamSequenceToken token = null)
        {
            logger.Info("OnNextAsync(item={0}, token={1})", item, token != null ? token.ToString() : "null");
            if (failPeriodTimer == null)
            {
                eventsConsumedCount++;
            }
            else if(failPeriodTimer.Elapsed >= failPeriod)
            {
                failPeriodTimer = null;
                eventsConsumedCount++;
            }
            else
            {
                eventsFailedCount++;
                throw new AggregateException("GO WAY!");
            }

            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            logger.Info("OnCompletedAsync()");
            return TaskDone.Done;
        }

        public Task OnErrorAsync(Exception ex)
        {
            logger.Info("OnErrorAsync({0})", ex);
            errorsCount++;
            return TaskDone.Done;
        }
    }
}
