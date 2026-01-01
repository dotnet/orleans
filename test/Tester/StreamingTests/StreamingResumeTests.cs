using Orleans.Streams;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.StreamingTests
{
    public abstract class StreamingResumeTests : TestClusterPerTest
    {
        protected static readonly TimeSpan StreamInactivityPeriod = TimeSpan.FromSeconds(5);
        protected static readonly TimeSpan MetadataMinTimeInCache = StreamInactivityPeriod * 100;
        protected static readonly TimeSpan DataMaxAgeInCache = StreamInactivityPeriod * 5;
        protected static readonly TimeSpan DataMinTimeInCache = StreamInactivityPeriod * 4;

        protected const string StreamProviderName = "StreamingCacheMissTests";

        /// <summary>
        /// Polls the grain's event counter until it reaches the expected count.
        /// This is needed for tests where the grain deactivates between events,
        /// which prevents WaitForEventCount from working reliably.
        /// </summary>
        protected static async Task PollForEventCount(IImplicitSubscriptionCounterGrain grain, int expectedCount, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (await grain.GetEventCounter() < expectedCount)
            {
                cts.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, cts.Token);
            }
        }

        [SkippableFact]
        public virtual async Task ResumeAfterInactivity()
        {
            await ResumeAfterInactivityImpl(false);
        }

        [SkippableFact]
        public virtual async Task ResumeAfterInactivityNotInCache()
        {
            await ResumeAfterInactivityImpl(true);
        }

        protected virtual async Task ResumeAfterInactivityImpl(bool waitForCacheToFlush)
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // Data that will be sent to the grains
            var interestingData = new byte[1] { 1 };

            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the first event
            await grain.WaitForEventCount(1, TimeSpan.FromSeconds(30));

            // Wait for the stream to become inactive
            await Task.Delay(StreamInactivityPeriod.Multiply(3));

            if (waitForCacheToFlush)
            {
                for (var i = 0; i < 5; i++)
                {
                    var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
                    await otherStream.OnNextAsync(interestingData);
                }
                // Wait a bit more for the cache to flush some events
                await Task.Delay(StreamInactivityPeriod.Multiply(3));
                for (var i = 0; i < 5; i++)
                {
                    var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
                    await otherStream.OnNextAsync(interestingData);
                }
            }

            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the second event
            await grain.WaitForEventCount(2, TimeSpan.FromSeconds(30));

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(2, await grain.GetEventCounter());
        }

        [SkippableFact]
        public virtual async Task ResumeAfterDeactivation()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // Data that will be sent to the grains
            var interestingData = new byte[1] { 1 };

            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the first event
            await grain.WaitForEventCount(1, TimeSpan.FromSeconds(30));

            // Wait for the stream to become inactive
            await Task.Delay(StreamInactivityPeriod.Multiply(3));
            await grain.Deactivate();

            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the second event
            await grain.WaitForEventCount(2, TimeSpan.FromSeconds(30));

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(2, await grain.GetEventCounter());
        }

        [SkippableFact]
        public virtual async Task ResumeAfterDeactivationActiveStream()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var otherStream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid());
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);
            await grain.DeactivateOnEvent(true);

            // Data that will be sent to the grains
            var interestingData = new byte[1] { 1 };

            await stream.OnNextAsync(interestingData);
            // Push other data
            await otherStream.OnNextAsync(interestingData);
            await otherStream.OnNextAsync(interestingData);
            await otherStream.OnNextAsync(interestingData);
            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the first 2 events on its stream.
            // Use polling because the grain deactivates after each event,
            // which prevents WaitForEventCount from working reliably.
            await PollForEventCount(grain, 2, TimeSpan.FromSeconds(30));

            // Wait for the stream to become inactive
            await Task.Delay(StreamInactivityPeriod.Multiply(3));
            await grain.Deactivate();

            await stream.OnNextAsync(interestingData);

            // Wait for the grain to receive the third event
            await PollForEventCount(grain, 3, TimeSpan.FromSeconds(30));

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(3, await grain.GetEventCounter());
        }

        [SkippableFact]
        public virtual async Task ResumeAfterSlowSubscriber()
        {
            var key = Guid.NewGuid();
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);
            var stream = streamProvider.GetStream<byte[]>("FastSlowImplicitSubscriptionCounterGrain", key);

            var fastGrain = this.Client.GetGrain<IFastImplicitSubscriptionCounterGrain>(key);
            var slowGrain = this.Client.GetGrain<ISlowImplicitSubscriptionCounterGrain>(key);

            await stream.OnNextAsync([1]);
            // Wait for the fast grain to receive the event instead of using a fixed delay
            await fastGrain.WaitForEventCount(1, TimeSpan.FromSeconds(30));
            Assert.Equal(1, await fastGrain.GetEventCounter());

            await stream.OnNextAsync([2]);
            // Wait for the fast grain to receive the second event
            await fastGrain.WaitForEventCount(2, TimeSpan.FromSeconds(30));
            Assert.Equal(2, await fastGrain.GetEventCounter());
        }
    }
}