using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.TestingHost;
using Tester.StreamingTests;
using Tester.TestStreamProviders;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils.Streaming
{
    public class AQClientStreamTests : TestClusterPerTest
    {
        private const string AQStreamProviderName = "AzureQueueProvider";
        private const string StreamNamespace = "AQSubscriptionMultiplicityTestsNamespace";

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public AQClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .AddAzureQueueStreams(AQStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                    .Configure<SiloMessagingOptions>(options => options.ClientDropTimeout = TimeSpan.FromSeconds(5));
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddAzureQueueStreams(AQStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                    .AddMemoryGrainStorage("PubSubStore");
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            if (this.HostedCluster != null)
            {
                var serviceId = this.HostedCluster.Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;
                AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(serviceId, AQStreamProviderName),
                    TestDefaultConfiguration.DataConnectionString).Wait();
                TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
            }
        }

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5639"), TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(AQStreamProviderName, StreamNamespace);
        }

        [SkippableFact(Skip = "AzureQueue has unpredictable event delivery counts - re-enable when we figure out how to handle this."), TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
        public async Task AQStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(AQStreamProviderName, StreamNamespace, output,
                    () => TestAzureTableStorageStreamFailureHandler.GetDeliveryFailureCount(AQStreamProviderName, NullLoggerFactory.Instance));
        }
    }
}
