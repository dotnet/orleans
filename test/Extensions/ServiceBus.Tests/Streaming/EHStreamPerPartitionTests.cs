using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
    public class EHStreamPerPartitionTests : OrleansTestingBase, IClassFixture<EHStreamPerPartitionTests.Fixture>
    {
        private readonly Fixture fixture;
        private const string StreamProviderName = "EHStreamPerPartition";
        private const string EHPath = "ehorleanstest5";
        private const string EHConsumerGroup = "orleansnightly";

        public class Fixture : BaseEventHubTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddEventHubStreams(StreamProviderName, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults(EHPath, EHConsumerGroup);
                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                                options.PersistInterval = TimeSpan.FromSeconds(1);
                            }));
                            b.UseDynamicClusterConfigDeploymentBalancer();
                            b.UseDataAdapter((s,n) => ActivatorUtilities.CreateInstance<StreamPerPartitionDataAdapter>(s));
                        });
                    hostBuilder
                        .AddMemoryGrainStorage("PubSubStore");
                }
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder
                        .AddEventHubStreams(StreamProviderName, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults(EHPath, EHConsumerGroup);
                            }));
                            b.UseDataAdapter((s, n) => ActivatorUtilities.CreateInstance<StreamPerPartitionDataAdapter>(s));
                        });
                }
            }
        }

        public EHStreamPerPartitionTests(Fixture fixture)
        {
            this.fixture = fixture;
            fixture.EnsurePreconditionsMet();
        }

        [SkippableFact(Skip = "Not sure what this test is testing, also the hacky test approach would make this test fail if there's any messages in the hub" +
                              "left from previous tests")]
        public async Task EH100StreamsTo4PartitionStreamsTest()
        {
            this.fixture.Logger.LogInformation("************************ EH100StreamsTo4PartitionStreamsTest *********************************");

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
                .Select( (consumer, i) => consumer.BecomeConsumer(StreamPerPartitionDataAdapter.GetPartitionGuid(i.ToString()), null, StreamProviderName))
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
