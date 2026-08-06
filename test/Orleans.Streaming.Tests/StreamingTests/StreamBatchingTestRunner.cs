using Orleans.Streams;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StreamingTests
{
    public abstract class StreamBatchingTestRunner
    {
        protected readonly BaseTestClusterFixture fixture;
        protected readonly ITestOutputHelper output;
        private readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        protected StreamBatchingTestRunner(BaseTestClusterFixture fixture, ITestOutputHelper output)
        {
            this.fixture = fixture;
            this.output = output;
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5649"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SingleSendBatchConsume()
        {
            const int ExpectedConsumed = 30;
            Guid streamGuid = Guid.NewGuid();
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(Timeout);

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(StreamBatchingTestConst.BatchingNameSpace, streamGuid);
            for(int i = 0; i< ExpectedConsumed; i++)
            {
                await stream.OnNextAsync(i.ToString());
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await observer.WaitForItemDeliveryCountAsync(StreamId.Create(StreamBatchingTestConst.BatchingNameSpace, streamGuid), ExpectedConsumed, StreamBatchingTestConst.ProviderName, cts.Token);
            await AssertCountersAsync(consumer, ExpectedConsumed, 2);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchSendSingleConsume()
        {
            const int BatchesSent = 3;
            const int ItemsPerBatch = 10;
            const int ExpectedConsumed = BatchesSent * ItemsPerBatch;
            Guid streamGuid = Guid.NewGuid();
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(Timeout);

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(StreamBatchingTestConst.NonBatchingNameSpace, streamGuid);
            for (int i = 0; i < BatchesSent; i++)
            {
                await stream.OnNextBatchAsync(Enumerable.Range(i, ItemsPerBatch).Select(v => v.ToString()));
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await observer.WaitForItemDeliveryCountAsync(StreamId.Create(StreamBatchingTestConst.NonBatchingNameSpace, streamGuid), ExpectedConsumed, StreamBatchingTestConst.ProviderName, cts.Token);
            await AssertCountersAsync(consumer, ExpectedConsumed, 1);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5632"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchSendBatchConsume()
        {
            const int BatchesSent = 3;
            const int ItemsPerBatch = 10;
            const int ExpectedConsumed = BatchesSent * ItemsPerBatch;
            Guid streamGuid = Guid.NewGuid();
            using var observer = StreamingDiagnosticObserver.Create();
            using var cts = new CancellationTokenSource(Timeout);

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(StreamBatchingTestConst.BatchingNameSpace, streamGuid);
            for (int i = 0; i < BatchesSent; i++)
            {
                await stream.OnNextBatchAsync(Enumerable.Range(i, ItemsPerBatch).Select(v => v.ToString()));
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await observer.WaitForItemDeliveryCountAsync(StreamId.Create(StreamBatchingTestConst.BatchingNameSpace, streamGuid), ExpectedConsumed, StreamBatchingTestConst.ProviderName, cts.Token);
            await AssertCountersAsync(consumer, ExpectedConsumed, ItemsPerBatch);
        }

        private async Task AssertCountersAsync(IStreamBatchingTestConsumerGrain consumer, int expectedConsumed, int minBatchSize)
        {
            ConsumptionReport report = await consumer.GetConsumptionReport();
            this.output.WriteLine($"Report - Consumed: {report.Consumed}, MaxBatchSize: {report.MaxBatchSize}");
            Assert.Equal(expectedConsumed, report.Consumed);
            Assert.True(report.MaxBatchSize >= minBatchSize);
        }
    }
}
