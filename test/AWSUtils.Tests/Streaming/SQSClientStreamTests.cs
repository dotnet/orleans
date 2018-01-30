using AWSUtils.Tests.StorageTests;
using Orleans.Providers.Streams;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using OrleansAWSUtils.Streams;

namespace AWSUtils.Tests.Streaming
{
    public class SQSClientStreamTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private string StorageConnectionString = AWSTestConstants.DefaultSQSConnectionString;

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public SQSClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            var clusterId = Guid.NewGuid().ToString();
            var streamConnectionString = new Dictionary<string, string>
            {
                {"DataConnectionString", StorageConnectionString},
                {"DeploymentId", clusterId}
            };
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

                legacy.ClusterConfiguration.Globals.ClusterId = clusterId;
                legacy.ClientConfiguration.ClusterId = clusterId;
                legacy.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
                legacy.ClientConfiguration.DataConnectionString = StorageConnectionString;
                legacy.ClusterConfiguration.Globals.DataConnectionString = StorageConnectionString;
                legacy.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
                legacy.ClientConfiguration.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
            });
        }

        public override void Dispose()
        {
            var clusterId = HostedCluster.ClusterId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, clusterId, StorageConnectionString, NullLoggerFactory.Instance).Wait();
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SQSStreamProviderName, StreamNamespace);
        }
    }
}
