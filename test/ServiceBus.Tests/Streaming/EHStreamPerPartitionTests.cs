using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.ServiceBus.Providers;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming")]
    public class EHStreamPerPartitionTests : OrleansTestingBase, IClassFixture<EHStreamPerPartitionTests.Fixture>
    {
        private readonly Fixture fixture;
        private const string StreamProviderName = "EHStreamPerPartition";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddPersistentStreams(StreamProviderName, StreamPerPartitionEventHubStreamAdapterFactory.Create, b=>
                        b.Configure<EventHubOptions>(ob => ob.Configure(options =>
                          {
                              options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                              options.ConsumerGroup = EHConsumerGroup;
                              options.Path = EHPath;
                          }))
                        .UseStaticClusterConfigDeploymentBalancer()
                        .ConfigureComponent<AzureTableStreamCheckpointerOptions, IStreamQueueCheckpointerFactory>(EventHubCheckpointerFactory.CreateFactory,
                            ob => ob.Configure(
                            options =>
                            {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.PersistInterval = TimeSpan.FromSeconds(1);
                        })));

                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddPersistentStreams(StreamProviderName, StreamPerPartitionEventHubStreamAdapterFactory.Create, b=>
                            b.Configure<EventHubOptions>(ob=>ob.Configure(
                            options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath;
                            })));
                }
            }
        }

        public EHStreamPerPartitionTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task EH100StreamsTo4PartitionStreamsTest()
        {
            this.fixture.Logger.Info("************************ EH100StreamsTo4PartitionStreamsTest *********************************");

            int streamCount = 100;
            int eventsInStream = 10;
            int partitionCount = 4;

            List<ISampleStreaming_ConsumerGrain> consumers = new List<ISampleStreaming_ConsumerGrain>(partitionCount);
            for (int i = 0; i < partitionCount; i++)
            {
                consumers.Add(this.fixture.GrainFactory.GetGrain<ISampleStreaming_ConsumerGrain>(Guid.NewGuid()));
            }

            // subscribe to each partition
            List<Task> becomeConsumersTasks = consumers
                .Select( (consumer, i) => consumer.BecomeConsumer(StreamPerPartitionEventHubStreamAdapterFactory.GetPartitionGuid(i.ToString()), null, StreamProviderName))
                .ToList();
            await Task.WhenAll(becomeConsumersTasks);

            await GenerateEvents(streamCount, eventsInStream);
            await TestingUtils.WaitUntilAsync(assertIsTrue => CheckCounters(consumers, streamCount * eventsInStream, assertIsTrue), TimeSpan.FromSeconds(30));
        }

        private async Task GenerateEvents(int streamCount, int eventsInStream)
        {
            IStreamProvider streamProvider = this.fixture.Client.GetStreamProvider(StreamProviderName);
            IAsyncStream<int>[] producers =
                Enumerable.Range(0, streamCount)
                    .Select(i => streamProvider.GetStream<int>(Guid.NewGuid(), null))
                    .ToArray();

            for (int i = 0; i < eventsInStream; i++)
            {
                // send event on each stream
                for (int j = 0; j < streamCount; j++)
                {
                    await producers[j].OnNextAsync(i);
                }
            }
        }

        private async Task<bool> CheckCounters(List<ISampleStreaming_ConsumerGrain> consumers, int totalEventCount, bool assertIsTrue)
        {
            List<Task<int>> becomeConsumersTasks = consumers
                .Select((consumer, i) => consumer.GetNumberConsumed())
                .ToList();
            int[] counts = await Task.WhenAll(becomeConsumersTasks);

            if (assertIsTrue)
            {
                // one stream per queue
                Assert.Equal(totalEventCount, counts.Sum());
            }
            else if (totalEventCount != counts.Sum())
            {
                return false;
            }
            return true;
        }
    }
}
