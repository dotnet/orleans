using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Filtering;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public abstract class StreamingCacheMissTests : TestClusterPerTest
    {
        protected static readonly TimeSpan DataMaxAgeInCache = TimeSpan.FromSeconds(5);
        protected static readonly TimeSpan DataMinTimeInCache = TimeSpan.FromSeconds(0);
        protected const string StreamProviderName = "StreamingCacheMissTests";

        private readonly ITestOutputHelper output;

        // Only deliver if item[0] == 1
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

        [SkippableFact]
        public virtual async Task PreviousEventEvictedFromCacheTest()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));

            // Data that will be sent to the grains
            var interestingData = new byte[1024];
            interestingData[0] = 1;

            // Should be delivered
            await stream.OnNextAsync(interestingData);

            // Wait a bit so cache expire, and launch a bunch of events to trigger the cleaning
            await Task.Delay(TimeSpan.FromSeconds(6));
            otherStreams.ForEach(s => s.OnNextAsync(interestingData));

            // Should be delivered
            await stream.OnNextAsync(interestingData);

            await Task.Delay(5_000);

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(2, await grain.GetEventCounter());
        }

        [SkippableFact]
        public virtual async Task PreviousEventEvictedFromCacheWithFilterTest()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), key);
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(nameof(IImplicitSubscriptionCounterGrain), Guid.NewGuid()));

            // Data that will always be filtered
            var skippedData = new byte[1024];
            skippedData[0] = 2;

            // Data that will be sent to the grains
            var interestingData = new byte[1024];
            interestingData[0] = 1;

            // Should not reach the grain
            await stream.OnNextAsync(skippedData);

            // Wait a bit so cache expire, and launch a bunch of events to trigger the cleaning
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
