
using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.StreamingTests
{
    public class SMSClientStreamTests : TestClusterPerTest
    {
        private const string SMSStreamProviderName = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public SMSClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(1);
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
            options.ClusterConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            options.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);

            options.ClientConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            return new TestCluster(options);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ SMSStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSStreamConsumerOnDroppedClientTest()
        {
            logger.Info("************************ SMSStreamConsumerOnDroppedClientTest *********************************");
            await runner.StreamConsumerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace, output);
        }
    }
}
