using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    /// <summary>
    /// Tests for stream caching behavior and cache miss scenarios.
    /// 
    /// Orleans streaming providers use caching to improve performance by:
    /// - Batching messages for delivery
    /// - Avoiding repeated deserialization
    /// - Enabling recovery from temporary failures
    /// 
    /// These tests verify correct behavior when:
    /// - Events are evicted from cache due to age or memory pressure
    /// - Filtered events interact with cache eviction
    /// - Multiple streams compete for cache space
    /// </summary>
    public abstract class StreamingCacheMissTests : TestClusterPerTest
    {
        protected static readonly TimeSpan DataMaxAgeInCache = TimeSpan.FromSeconds(5);
        protected static readonly TimeSpan DataMinTimeInCache = TimeSpan.FromSeconds(0);
        protected const string StreamProviderName = "StreamingCacheMissTests";

        private readonly ITestOutputHelper output;

        /// <summary>
        /// Custom stream filter that only delivers messages where the first byte equals 1.
        /// Used to test interaction between filtering and cache eviction - filtered
        /// messages should not trigger grain activation or affect cache behavior.
        /// </summary>
        protected class CustomStreamFilter : IStreamFilter
        {
            public bool ShouldDeliver(StreamId streamId, object item, string filterData)
            {
                var data = item as byte[];
                return data == default || data[0] == 1;
            }
        }

        public StreamingCacheMissTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests that grains correctly handle events that were evicted from cache.
        /// Scenario:
        /// 1. Send an event that gets cached
        /// 2. Wait for cache expiration and trigger eviction
        /// 3. Send another event
        /// Verifies that both events are delivered despite cache eviction.
        /// </summary>
        [SkippableFact]
        public virtual async Task PreviousEventEvictedFromCacheTest()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            // This ensures cache pressure and increases likelihood of eviction
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));

            // Data that will be sent to the grains
            var interestingData = new byte[1024];
            interestingData[0] = 1;

            // Should be delivered
            await stream.OnNextAsync(interestingData);

            // Wait for cache expiration time to pass
            // Then send events to other streams to trigger cache cleaning/eviction
            await Task.Delay(TimeSpan.FromSeconds(6));
            otherStreams.ForEach(s => s.OnNextAsync(interestingData));

            // Should be delivered
            await stream.OnNextAsync(interestingData);

            await Task.Delay(5_000);

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(2, await grain.GetEventCounter());
        }

        /// <summary>
        /// Tests cache eviction behavior with filtered events.
        /// Verifies that:
        /// - Filtered events don't prevent cache eviction
        /// - Non-filtered events are still delivered after cache eviction
        /// - Filter state doesn't interfere with cache management
        /// </summary>
        [SkippableFact]
        public virtual async Task PreviousEventEvictedFromCacheWithFilterTest()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            // This ensures cache pressure and increases likelihood of eviction
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));

            // Data that will always be filtered
            var skippedData = new byte[1024];
            skippedData[0] = 2;

            // Data that will be sent to the grains
            var interestingData = new byte[1024];
            interestingData[0] = 1;

            // Send filtered data that should not reach the grain
            await stream.OnNextAsync(skippedData);

            // Wait for cache expiration and trigger eviction with more filtered events
            // This tests that filtered events in cache don't affect delivery guarantees
            await Task.Delay(TimeSpan.FromSeconds(6));
            otherStreams.ForEach(s => s.OnNextAsync(skippedData));

            // Should be delivered
            await stream.OnNextAsync(interestingData);

            await Task.Delay(1000);

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(1, await grain.GetEventCounter());
        }
    }
}
