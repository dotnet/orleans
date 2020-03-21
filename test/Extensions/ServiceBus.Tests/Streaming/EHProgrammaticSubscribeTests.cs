using System;
using Orleans.Hosting;
using Orleans.Streams;
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
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
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
                                options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath;
                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.PersistInterval = TimeSpan.FromSeconds(10);
                            }));
                        });

                    hostBuilder
                        .AddEventHubStreams(StreamProviderName2, b=>
                        {
                            b.ConfigureEventHub(ob => ob.Configure(options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                                options.ConsumerGroup = EHConsumerGroup;
                                options.Path = EHPath2;

                            }));
                            b.UseAzureTableCheckpointer(ob => ob.Configure(options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.PersistInterval = TimeSpan.FromSeconds(10);
                            }));
                        });

                    hostBuilder
                          .AddMemoryGrainStorage("PubSubStore");
                }
            }
        }

        public EHProgrammaticSubscribeTest(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
