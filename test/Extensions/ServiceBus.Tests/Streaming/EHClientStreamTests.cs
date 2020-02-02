using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.Hosting;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime.Configuration;
using Orleans.Streaming.EventHubs;
using Orleans.TestingHost;
using Tester.TestStreamProviders;
using ServiceBus.Tests.TestStreamProviders.EventHub;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Orleans.Streams;
using Orleans.ServiceBus.Providers;
using Tester;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ServiceBus.Tests.StreamingTests
{
    [TestCategory("EventHub"), TestCategory("Streaming"), TestCategory("Functional")]
    public class EHClientStreamTests : TestClusterPerTest
    {
        private const string StreamProviderName = "EventHubStreamProvider";
        private const string StreamNamespace = "StreamNamespace";
        private const string EHPath = "ehorleanstest";
        private const string EHConsumerGroup = "orleansnightly";

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;
        public EHClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForEventHub();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddPersistentStreams(StreamProviderName, TestEventHubStreamAdapterFactory.Create, b=>
                    {
                        b.Configure<SiloMessagingOptions>(ob => ob.Configure(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5)));
                        b.Configure<EventHubOptions>(ob => ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        }));
                        b.ConfigureComponent<AzureTableStreamCheckpointerOptions, IStreamQueueCheckpointerFactory>(
                            EventHubCheckpointerFactory.CreateFactory,
                            ob => ob.Configure(options =>
                            {
                                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                                options.PersistInterval = TimeSpan.FromSeconds(10);
                            }));
                    })
                    .AddMemoryGrainStorage("PubSubStore")
                    .ConfigureServices(services => services.TryAddSingleton<IEventHubDataAdapter, EventHubDataAdapter>());
            }
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddPersistentStreams(StreamProviderName, TestEventHubStreamAdapterFactory.Create, b=>b
                        .Configure<EventHubOptions>(ob=>ob.Configure(options =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.EventHubConnectionString;
                            options.ConsumerGroup = EHConsumerGroup;
                            options.Path = EHPath;
                        })))
                    .ConfigureServices(services => services.TryAddSingleton<IEventHubDataAdapter, EventHubDataAdapter>());
            }
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5657")]
        public async Task EHStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(StreamProviderName, StreamNamespace);
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5634")]
        public async Task EHStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ EHStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(StreamProviderName, StreamNamespace, output,
                    () => TestAzureTableStorageStreamFailureHandler.GetDeliveryFailureCount(StreamProviderName, NullLoggerFactory.Instance), true);
        }
    }
}
