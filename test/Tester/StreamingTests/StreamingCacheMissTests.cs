using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
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

        // Custom batch container that enable filtering for all provider
        protected class CustomBatchContainer : IBatchContainer
        {
            private IBatchContainer batchContainer;

            public CustomBatchContainer(IBatchContainer batchContainer)
            {
                this.batchContainer = batchContainer;
            }

            public StreamSequenceToken SequenceToken => this.batchContainer.SequenceToken;

            public StreamId StreamId => batchContainer.StreamId;

            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => this.batchContainer.GetEvents<T>();

            public bool ImportRequestContext() => this.batchContainer.ImportRequestContext();
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
            var stream = streamProvider.GetStream<byte[]>(key, nameof(IImplicitSubscriptionCounterGrain));
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(Guid.NewGuid(), nameof(IImplicitSubscriptionCounterGrain)));

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

            await Task.Delay(1000);

            Assert.Equal(0, await grain.GetErrorCounter());
            Assert.Equal(2, await grain.GetEventCounter());
        }

        [SkippableFact]
        public virtual async Task PreviousEventEvictedFromCacheWithFilterTest()
        {
            var streamProvider = this.Client.GetStreamProvider(StreamProviderName);

            // Tested stream and corresponding grain
            var key = Guid.NewGuid();
            var stream = streamProvider.GetStream<byte[]>(key, nameof(IImplicitSubscriptionCounterGrain));
            var grain = this.Client.GetGrain<IImplicitSubscriptionCounterGrain>(key);

            // We need multiple streams, so at least another one will be handled by the same PullingAgent than "stream"
            var otherStreams = new List<IAsyncStream<byte[]>>();
            for (var i = 0; i < 20; i++)
                otherStreams.Add(streamProvider.GetStream<byte[]>(Guid.NewGuid(), nameof(IImplicitSubscriptionCounterGrain)));

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
