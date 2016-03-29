using System;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using UnitTests.Tester;
using Xunit;

namespace Tester.StreamingTests
{
    public class SMSClientStreamTests : TestClusterPerTest
    {
        private const string SMSStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private ClientStreamTestRunner runner;

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            options.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);

            options.ClientConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            return new TestCluster(options);
        }

        public SMSClientStreamTests()
        {
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task MSMStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace);
        }
    }
}
