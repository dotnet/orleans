
using System;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime.Development;
using Orleans.TestingHost.Utils;
using UnitTests.GrainInterfaces;

namespace Tester.StreamingTests
{
    public abstract class PersistentStreamQueueRebalancingTestRunner
    {
        private static TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly ITestOutputHelper output;
        private readonly IGrainFactory grainFactory;
        private readonly string streamProviderName;

        protected PersistentStreamQueueRebalancingTestRunner(string streamProviderName, IGrainFactory grainFactory, ITestOutputHelper output)
        {
            this.streamProviderName = streamProviderName;
            this.output = output;
            this.grainFactory = grainFactory;
        }

        public static ISiloHostBuilder ConfigureSiloHostBuilder(ISiloHostBuilder hostBuilder)
        {
            return hostBuilder.UseInMemoryLeaseProvider()
                              .AddMemoryGrainStorage("PubSubStore");
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Streams")]
        public async Task RebalanceQueuesWhileStreamingTest()
        {
            var streamGuid = Guid.NewGuid();

            var producer = this.grainFactory.GetGrain<ISampleStreaming_ProducerGrain>(Guid.NewGuid());
            await producer.BecomeProducer(streamGuid, "bob", this.streamProviderName);

            var consumer = this.grainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer.BecomeConsumer(streamGuid, "bob", this.streamProviderName);

            IDevelopmentLeaseProviderGrain leaseProviderGrain = InMemoryLeaseProvider.GetLeaseProviderGrain(this.grainFactory);

            await producer.StartPeriodicProducing();
            await Task.Delay(TimeSpan.FromSeconds(10));
            // reset leases to force rebalance
            await leaseProviderGrain.Reset();
            await Task.Delay(TimeSpan.FromSeconds(10));
            // make sure new consumers can subscribe after rebalance.
            var consumer2 = this.grainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid());
            await consumer2.BecomeConsumer(streamGuid, "bob", this.streamProviderName);
            await producer.StopPeriodicProducing();

            await TestingUtils.WaitUntilAsync(lastTry => CheckCounters(producer, consumer, lastTry), Timeout);
        }

        private async Task<bool> CheckCounters(ISampleStreaming_ProducerGrain producer, ISampleStreaming_ConsumerGrain consumer, bool assertIsTrue)
        {
            int numProduced = await producer.GetNumberProduced();
            int numConsumed = await consumer.GetNumberConsumed();
            if (assertIsTrue)
            {
                Assert.True(numProduced > 0, "Events were not produced");
                Assert.Equal(numProduced, numConsumed);
            }
            return (numProduced > 0 && numProduced == numConsumed);
        }
    }
}
