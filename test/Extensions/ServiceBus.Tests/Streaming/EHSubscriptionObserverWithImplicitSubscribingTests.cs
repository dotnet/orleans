using System;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Streams;
using Orleans.TestingHost;
using Tester.StreamingTests.ProgrammaticSubscribeTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
    public class EHSubscriptionObserverWithImplicitSubscribingTests : SubscriptionObserverWithImplicitSubscribingTestRunner, IClassFixture<EHSubscriptionObserverWithImplicitSubscribingTests.Fixture>
    {
        private const string EHPath = "ehorleanstest8";
        private const string EHPath2 = "ehorleanstest9";
        private const string EHConsumerGroup = "orleansnightly";

        public class Fixture : BaseEventHubTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .AddEventHubStreams(StreamProviderName, b => b
                        .ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }))
                        .UseAzureTableCheckpointer(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }))
                        .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));

                hostBuilder
                    .AddEventHubStreams(StreamProviderName2, b => b
                        .ConfigureEventHub(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath2;

                        }))
                        .UseAzureTableCheckpointer(ob => ob.Configure(options => {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.PersistInterval = TimeSpan.FromSeconds(10);
                        }))
                        .ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly));

                hostBuilder
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        public EHSubscriptionObserverWithImplicitSubscribingTests(ITestOutputHelper output, Fixture fixture)
            : base(fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
