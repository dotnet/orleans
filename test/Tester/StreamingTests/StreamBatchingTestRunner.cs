using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams;
using Orleans.TestingHost.Utils;
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
        private TimeSpan Timeout = TimeSpan.FromSeconds(30);

        protected StreamBatchingTestRunner(BaseTestClusterFixture fixture, ITestOutputHelper output)
        {
            this.fixture = fixture;
            this.output = output;
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5649"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SingleSendBatchConsume()
        {
            const int ExpectedConsumed = 30;
            Guid streamGuid = Guid.NewGuid();

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(streamGuid, StreamBatchingTestConst.BatchingNameSpace);
            for(int i = 0; i< ExpectedConsumed; i++)
            {
                await stream.OnNextAsync(i.ToString());
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(consumer, ExpectedConsumed, 2, lastTry), Timeout);
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchSendSingleConsume()
        {
            const int BatchesSent = 3;
            const int ItemsPerBatch = 10;
            const int ExpectedConsumed = BatchesSent * ItemsPerBatch;
            Guid streamGuid = Guid.NewGuid();

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(streamGuid, StreamBatchingTestConst.NonBatchingNameSpace);
            for (int i = 0; i < BatchesSent; i++)
            {
                await stream.OnNextBatchAsync(Enumerable.Range(i, ItemsPerBatch).Select(v => v.ToString()));
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(consumer, ExpectedConsumed, 1, lastTry), Timeout);
        }

        [SkippableFact(Skip = "https://github.com/dotnet/orleans/issues/5632"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task BatchSendBatchConsume()
        {
            const int BatchesSent = 3;
            const int ItemsPerBatch = 10;
            const int ExpectedConsumed = BatchesSent * ItemsPerBatch;
            Guid streamGuid = Guid.NewGuid();

            IStreamProvider provider = this.fixture.Client.GetStreamProvider(StreamBatchingTestConst.ProviderName);
            IAsyncStream<string> stream = provider.GetStream<string>(streamGuid, StreamBatchingTestConst.BatchingNameSpace);
            for (int i = 0; i < BatchesSent; i++)
            {
                await stream.OnNextBatchAsync(Enumerable.Range(i, ItemsPerBatch).Select(v => v.ToString()));
            }
            var consumer = this.fixture.GrainFactory.GetGrain<IStreamBatchingTestConsumerGrain>(streamGuid);
            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(consumer, ExpectedConsumed, ItemsPerBatch, lastTry), Timeout);
        }

        private async Task<bool> CheckCounters(IStreamBatchingTestConsumerGrain consumer, int expectedConsumed, int minBatchSize, bool assertIsTrue)
        {
            ConsumptionReport report = await consumer.GetConsumptionReport();
            this.output.WriteLine($"Report - Consumed: {report.Consumed}, MaxBatchSize: {report.MaxBatchSize}");
            if (assertIsTrue)
            {
                Assert.Equal(expectedConsumed, report.Consumed);
                Assert.True(report.MaxBatchSize >= minBatchSize);
                return true;
            }
            else
            {
                return report.Consumed == expectedConsumed && report.MaxBatchSize >= minBatchSize;
            }
        }
    }
}
