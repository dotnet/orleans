using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("AzureStorage"), TestCategory("Storage"), TestCategory("Streaming")]
    public class AQSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string AQStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private const string StreamNamespace = "AQSubscriptionMultiplicityTestsNamespace";
        private SubscriptionMultiplicityTestRunner runner;
        private const int queueCount = 8;
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
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
            }
        }

        private class MySiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                     .AddMemoryGrainStorage("PubSubStore")
                    .AddAzureQueueStreams(AQStreamProviderName, ob=>ob.Configure<IOptions<ClusterOptions>>(
                        (options, dep) =>
                        {
                            options.ConfigureTestDefaults();
                            options.QueueNames = AzureQueueUtilities.GenerateQueueNames(dep.Value.ClusterId, queueCount);
                        }));
            }
        }

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            runner = new SubscriptionMultiplicityTestRunner(AQStreamProviderName, this.HostedCluster);
        }

        public override async Task DisposeAsync()
        {
            await base.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(TestDefaultConfiguration.DataConnectionString))
            {
                await AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(
                    NullLoggerFactory.Instance,
                    AzureQueueUtilities.GenerateQueueNames(this.HostedCluster.Options.ClusterId, queueCount),
                    new AzureQueueOptions().ConfigureTestDefaults());
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleParallelSubscriptionTest()
        {
            logger.LogInformation("************************ AQMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleLinearSubscriptionTest()
        {
            logger.LogInformation("************************ AQMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleSubscriptionTest_AddRemove()
        {
            logger.LogInformation("************************ AQMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQResubscriptionTest()
        {
            logger.LogInformation("************************ AQResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQResubscriptionAfterDeactivationTest()
        {
            logger.LogInformation("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQActiveSubscriptionTest()
        {
            logger.LogInformation("************************ AQActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQTwoIntermitentStreamTest()
        {
            logger.LogInformation("************************ AQTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQSubscribeFromClientTest()
        {
            logger.LogInformation("************************ AQSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
