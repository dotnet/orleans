
using System;
using System.IO;
using System.Threading.Tasks;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;
using Xunit;

namespace Tester.StreamingTests
{
    public class SMSClientStreamTests : HostedTestClusterPerTest
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private ClientStreamTestRunner runner;

        public override TestingSiloHost CreateSiloHost()
        {
            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansConfigurationForTesting.xml"),
                AdjustConfig = config =>
                {
                    config.AddMemoryStorageProvider("PubSubStore");
                    config.Globals.RegisterStreamProvider<SimpleMessageStreamProvider>(SMSStreamProviderName);
                    config.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
                }
            };

            var clientOptions = new TestingClientOptions
            {
                ClientConfigFile = new FileInfo("ClientConfigurationForTesting.xml"),
                AdjustConfig = config =>
                {
                    config.RegisterStreamProvider<SimpleMessageStreamProvider>(SMSStreamProviderName);
                }
            };

            var testHost = new TestingSiloHost(siloOptions, clientOptions);
            runner = new ClientStreamTestRunner(testHost);

            return testHost;
        }

        [Fact, TestCategory("BVT"), TestCategory("Streaming")]
        public async Task MSMStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SMSStreamProviderName, StreamNamespace);
        }
    }
}
