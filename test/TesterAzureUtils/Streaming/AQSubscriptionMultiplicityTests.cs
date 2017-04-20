using System;
using System.Threading.Tasks;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace Tester.AzureUtils.Streaming
{
    [TestCategory("Azure"), TestCategory("Storage"), TestCategory("Streaming")]
    public class AQSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string AQStreamProviderName = StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME;
        private const string StreamNamespace = "AQSubscriptionMultiplicityTestsNamespace";
        private readonly SubscriptionMultiplicityTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            TestUtils.CheckForAzureStorage();
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddAzureQueueStreamProvider(AQStreamProviderName);
            options.ClientConfiguration.AddAzureQueueStreamProvider(AQStreamProviderName);
            return new TestCluster(options);
        }

        public AQSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(AQStreamProviderName, this.HostedCluster);
        }

        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(AQStreamProviderName, deploymentId, TestDefaultConfiguration.DataConnectionString).Wait();
        }
        
        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleParallelSubscriptionTest()
        {
            logger.Info("************************ AQMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleLinearSubscriptionTest()
        {
            logger.Info("************************ AQMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQMultipleSubscriptionTest_AddRemove()
        {
            logger.Info("************************ AQMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQResubscriptionTest()
        {
            logger.Info("************************ AQResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQActiveSubscriptionTest()
        {
            logger.Info("************************ AQActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQTwoIntermitentStreamTest()
        {
            logger.Info("************************ AQTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AQSubscribeFromClientTest()
        {
            logger.Info("************************ AQSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
