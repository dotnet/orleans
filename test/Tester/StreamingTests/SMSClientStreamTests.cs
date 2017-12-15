
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

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");
                legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
                legacy.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);

                legacy.ClientConfiguration.AddSimpleMessageStreamProvider(SMSStreamProviderName);
            });
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
