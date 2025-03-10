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

            await Task.Delay(1_000);

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

            await Task.Delay(2_000);

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

            await Task.Delay(1_000);

            // Wait for the stream to become inactive
            await Task.Delay(StreamInactivityPeriod.Multiply(3));
            await grain.Deactivate();

            await stream.OnNextAsync(interestingData);

            await Task.Delay(2_000);

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

            await Task.Delay(1_000);

            // Wait for the stream to become inactive
            await Task.Delay(StreamInactivityPeriod.Multiply(3));
            await grain.Deactivate();

            await stream.OnNextAsync(interestingData);

            await Task.Delay(2_000);

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
            await Task.Delay(500);
            Assert.Equal(1, await fastGrain.GetEventCounter());

            await stream.OnNextAsync([2]);
            await Task.Delay(500);
            Assert.Equal(2, await fastGrain.GetEventCounter());
        }
    }
}