using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
        private ILogger logger;
        private StreamSubscriptionHandle<int> consumerHandle;
        private Stopwatch failPeriodTimer;
        private TimeSpan failPeriod;

        public FaultableConsumerGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger("FaultableConsumerGrain " + base.IdentityString);
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("OnActivateAsync");
            Reset();
            return Task.CompletedTask;
        }

        private void Reset()
        {
            eventsConsumedCount = 0;
            errorsCount = 0;
            eventsFailedCount = 0;
            consumerHandle = null;
            failPeriodTimer = null;
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            logger.LogInformation("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task BecomeConsumer(Guid streamId, string streamNamespace, string providerToUse)
        {
            logger.LogInformation("BecomeConsumer");
            IStreamProvider streamProvider = this.GetStreamProvider(providerToUse);
            consumer = streamProvider.GetStream<int>(streamNamespace, streamId);
            consumerHandle = await consumer.SubscribeAsync(
                OnNextAsync,
                OnErrorAsync,
                () =>
                {
                    Reset();
                    return Task.CompletedTask;
                });
        }

        public Task SetFailPeriod(TimeSpan failurePeriod)
        {
            failPeriod = failurePeriod;
            failPeriodTimer = Stopwatch.StartNew();
            return Task.CompletedTask;
        }

        public async Task StopConsuming()
        {
            logger.LogInformation("StopConsuming");
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
            logger.LogInformation("OnNextAsync(item={Item}, token={Token})", item, token != null ? token.ToString() : "null");
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

            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            logger.LogInformation("OnCompletedAsync()");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            logger.LogInformation(ex, "OnErrorAsync()");
            errorsCount++;
            return Task.CompletedTask;
        }
    }
}
