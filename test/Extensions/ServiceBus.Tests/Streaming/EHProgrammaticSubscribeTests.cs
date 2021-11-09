using System;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.Streaming
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
    public class EHProgrammaticSubscribeTest : ProgrammaticSubcribeTestsRunner, IClassFixture<EHProgrammaticSubscribeTest.Fixture>
    {
        private const string EHPath = "ehorleanstest4";
        private const string EHPath2 = "ehorleanstest3";
        private const string EHConsumerGroup = "orleansnightly";
        public class Fixture : BaseEventHubTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<TestClusterConfigurator>();
                builder.AddClientBuilderConfigurator<TestClusterConfigurator>();
            }

            private class TestClusterConfigurator : ISiloConfigurator, IClientBuilderConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder
                        .AddEventHubStreams(StreamProviderName, b=>
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
                                options.PersistInterval = TimeSpan.FromSeconds(10);
                            }));
                        });

                    hostBuilder
                        .AddEventHubStreams(StreamProviderName2, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConfigureTestDefaults();
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath2;

                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
                                options.PersistInterval = TimeSpan.FromSeconds(10);
                            }));
                        });

                    hostBuilder
                          .AddMemoryGrainStorage("PubSubStore");
                }

                public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) => clientBuilder.AddStreaming();
            }
        }

        public EHProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
