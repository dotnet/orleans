using DistributedTests.GrainInterfaces;
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streams.Core;

namespace DistributedTests.Grains
{
    [ImplicitStreamSubscription(StreamingConstants.StreamingNamespace)]
    public class ImplicitSubscriberGrain : Grain, IImplicitSubscriberGrain, IStreamSubscriptionObserver, IAsyncObserver<object>
    {
        private readonly ILogger _logger;
        private int _requestCounter;
        private int _errorCounter;

        public ImplicitSubscriberGrain(ILogger<ImplicitSubscriberGrain> logger)
        {
            _logger = logger;
        }

        public Task<int> GetCounterValue(string counterName)
        {
            return counterName switch
            {
                "requests" => Task.FromResult(_requestCounter),
                "errors" => Task.FromResult(_errorCounter),
                _ => throw new ArgumentOutOfRangeException(nameof(counterName)),
            };
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex)
        {
            _logger.LogError(ex, "OnErrorAsync");
            _errorCounter++;
            return Task.CompletedTask;
        }

        public Task OnNextAsync(object item, StreamSequenceToken token = null)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("OnNextAsync {Item}", item);

            _requestCounter++;

            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            if (_logger.IsEnabled(LogLevel.Trace)) _logger.LogTrace("OnSubscribed {StreamId}", handleFactory.StreamId);

            await GrainFactory
                .GetGrain<ICounterGrain>(StreamingConstants.DefaultCounterGrain)
                .Track(this.AsReference<IGrainWithCounter>());

            await handleFactory
                .Create<object>()
                .ResumeAsync(this);
        }
    }
}
