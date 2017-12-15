using AWSUtils.Tests.StorageTests;
using Orleans;
using Orleans.Providers.Streams;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;

namespace AWSUtils.Tests.Streaming
{
    public class SQSSubscriptionMultiplicityTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private string StreamConnectionString = AWSTestConstants.DefaultSQSConnectionString;
        private readonly SubscriptionMultiplicityTestRunner runner;

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            var clusterId = Guid.NewGuid().ToString();
            var streamConnectionString = new Dictionary<string, string>
            {
                {"DataConnectionString", StreamConnectionString},
                {"DeploymentId", clusterId}
            };

            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                legacy.ClusterConfiguration.Globals.ClusterId = clusterId;
                legacy.ClientConfiguration.ClusterId = clusterId;
                legacy.ClientConfiguration.DataConnectionString = StreamConnectionString;
                legacy.ClusterConfiguration.Globals.DataConnectionString = StreamConnectionString;
                legacy.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
                legacy.ClientConfiguration.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
            });
        }

        public SQSSubscriptionMultiplicityTests()
        {
            runner = new SubscriptionMultiplicityTestRunner(SQSStreamProviderName, this.HostedCluster);
        }

        public override void Dispose()
        {
            var clusterId = HostedCluster.ClusterId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, clusterId, StreamConnectionString, NullLoggerFactory.Instance).Wait();
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleParallelSubscriptionTest()
        {
            logger.Info("************************ SQSMultipleParallelSubscriptionTest *********************************");
            await runner.MultipleParallelSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleLinearSubscriptionTest()
        {
            logger.Info("************************ SQSMultipleLinearSubscriptionTest *********************************");
            await runner.MultipleLinearSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSMultipleSubscriptionTest_AddRemove()
        {
            logger.Info("************************ SQSMultipleSubscriptionTest_AddRemove *********************************");
            await runner.MultipleSubscriptionTest_AddRemove(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionTest()
        {
            logger.Info("************************ SQSResubscriptionTest *********************************");
            await runner.ResubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSResubscriptionAfterDeactivationTest()
        {
            logger.Info("************************ ResubscriptionAfterDeactivationTest *********************************");
            await runner.ResubscriptionAfterDeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSActiveSubscriptionTest()
        {
            logger.Info("************************ SQSActiveSubscriptionTest *********************************");
            await runner.ActiveSubscriptionTest(Guid.NewGuid(), StreamNamespace);
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSTwoIntermitentStreamTest()
        {
            logger.Info("************************ SQSTwoIntermitentStreamTest *********************************");
            await runner.TwoIntermitentStreamTest(Guid.NewGuid());
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSSubscribeFromClientTest()
        {
            logger.Info("************************ SQSSubscribeFromClientTest *********************************");
            await runner.SubscribeFromClientTest(Guid.NewGuid(), StreamNamespace);
        }
    }
}
