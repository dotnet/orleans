
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions.Common;
using Microsoft.Extensions.Configuration;
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
        private static string serviceId = Guid.NewGuid().ToString();

        public AQClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            });
            builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<MyClientBuilderConfigurator>();
        }

        private class MyClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .Configure<ClusterOptions>(op => op.ServiceId = serviceId)
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(AQStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }));
            }
        }

        private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder
                    .Configure<ClusterOptions>(op => op.ServiceId = serviceId)
                    .AddAzureQueueStreams<AzureQueueDataAdapterV2>(AQStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
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
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(NullLoggerFactory.Instance, AzureQueueStreamProviderUtils.GenerateDefaultAzureQueueNames(serviceId, AQStreamProviderName),
                TestDefaultConfiguration.DataConnectionString).Wait();
            TestAzureTableStorageStreamFailureHandler.DeleteAll().Wait();
        }

        [SkippableFact, TestCategory("Functional"), TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
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
