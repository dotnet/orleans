#nullable enable
using Microsoft.Extensions.Logging;
using Orleans.Streams;
using Orleans.Streams.Core;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(nameof(IImplicitSubscriptionCounterGrain))]
    public class ImplicitSubscriptionCounterGrain : Grain<ImplicitSubscriptionCounterGrain.MyState>, IImplicitSubscriptionCounterGrain, IStreamSubscriptionObserver
    {
        private readonly ILogger logger;
        private bool deactivateOnEvent;
        private TaskCompletionSource<int>? _eventCountWaiter;
        private int _expectedEventCount;

        [GenerateSerializer]
        public class MyState
        {
            [Id(0)]
            public int EventCounter { get; set; }
            [Id(1)]
            public int ErrorCounter { get; set; }
            [Id(2)]
            public StreamSequenceToken? Token { get; set; }
        }

        public ImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{nameof(ImplicitSubscriptionCounterGrain)} {this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.logger.LogInformation("OnActivateAsync");
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            this.logger.LogInformation($"OnDeactivateAsync: {reason}");
            return base.OnDeactivateAsync(reason, cancellationToken);
        }

        public Task<int> GetErrorCounter() => Task.FromResult(this.State.ErrorCounter);

        public Task<int> GetEventCounter() => Task.FromResult(this.State.EventCounter);

        public async Task<int> WaitForEventCount(int expectedCount, TimeSpan timeout)
        {
            // If we already have enough events, return immediately
            if (this.State.EventCounter >= expectedCount)
            {
                return this.State.EventCounter;
            }

            // Set up a waiter for the expected count
            _expectedEventCount = expectedCount;
            _eventCountWaiter = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var cts = new CancellationTokenSource(timeout);
            // Note: Cannot access this.State in the callback as it runs on a timer thread without grain context.
            // Only capture the expectedCount which is a local variable.
            using var registration = cts.Token.Register(() =>
                _eventCountWaiter?.TrySetException(new TimeoutException($"Timed out waiting for event count {expectedCount}.")));

            try
            {
                return await _eventCountWaiter.Task;
            }
            finally
            {
                _eventCountWaiter = null;
            }
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            this.logger.LogInformation($"OnSubscribed: {handleFactory.ProviderName}/{handleFactory.StreamId}");

            await handleFactory.Create<byte[]>().ResumeAsync(OnNext, OnError, OnCompleted, this.State.Token);

            async Task OnNext(byte[] value, StreamSequenceToken token)
            {
                this.logger.LogInformation("Received: [{Value} {Token}]", value, token);
                this.State.EventCounter++;
                this.State.Token = token;
                await this.WriteStateAsync();

                // Signal any waiters if we've reached the expected count
                if (_eventCountWaiter != null && this.State.EventCounter >= _expectedEventCount)
                {
                    _eventCountWaiter.TrySetResult(this.State.EventCounter);
                }

                if (this.deactivateOnEvent)
                {
                    this.DeactivateOnIdle();
                }
            }

            async Task OnError(Exception ex)
            {
                this.logger.LogError("Error: {Exception}", ex);
                this.State.ErrorCounter++;
                await this.WriteStateAsync();
            }

            Task OnCompleted() => Task.CompletedTask;
        }

        public Task DeactivateOnEvent(bool deactivate)
        {
            this.deactivateOnEvent = deactivate;
            return Task.CompletedTask;
        }
    }

    [ImplicitStreamSubscription("FastSlowImplicitSubscriptionCounterGrain")]
    public class FastImplicitSubscriptionCounterGrain : ImplicitSubscriptionCounterGrain, IFastImplicitSubscriptionCounterGrain
    {
        public FastImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }
    }

    [ImplicitStreamSubscription("FastSlowImplicitSubscriptionCounterGrain")]
    public class SlowImplicitSubscriptionCounterGrain : ImplicitSubscriptionCounterGrain, ISlowImplicitSubscriptionCounterGrain
    {
        public SlowImplicitSubscriptionCounterGrain(ILoggerFactory loggerFactory) : base(loggerFactory)
        {
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(10_000);
            await base.OnActivateAsync(cancellationToken);
        }
    }
}