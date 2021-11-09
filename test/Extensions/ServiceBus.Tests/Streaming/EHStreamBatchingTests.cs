using System;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.Streaming
{
    [TestCategory("EventHub")]
    public class EHStreamBatchingTests : StreamBatchingTestRunner, IClassFixture<EHStreamBatchingTests.Fixture>
    {
        public class Fixture : BaseEventHubTestClusterFixture
        {
            private const string EHPath = "ehorleanstest7";
            private const string EHConsumerGroup = "orleansnightly";

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
                        .AddEventHubStreams(StreamBatchingTestConst.ProviderName, b =>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults();
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath;
                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                                options.PersistInterval = TimeSpan.FromSeconds(1);
                            }));
                            b.UseDynamicClusterConfigDeploymentBalancer();
                            b.ConfigurePullingAgent(ob => ob.Configure(options =>
                            {
                                options.BatchContainerBatchSize = 10;
                            }));
                            b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                        });
                    hostBuilder
                        .AddMemoryGrainStorageAsDefault();
                }
            }

            private class MyClientBuilderConfigurator : IClientBuilderConfigurator
            {
                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
                {
                    clientBuilder.AddEventHubStreams(StreamBatchingTestConst.ProviderName, b =>
                    {
                        b.ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConfigureTestDefaults();
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }));
                        b.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    });
                }
            }
        }

        public EHStreamBatchingTests(Fixture fixture, ITestOutputHelper output)
            : base(fixture, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
