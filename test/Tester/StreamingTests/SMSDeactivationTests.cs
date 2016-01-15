using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Orleans;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    public class SMSDeactivationTests : HostedTestClusterPerTest
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private DeactivationTestRunner runner;
        
        public SMSDeactivationTests()
        {
            runner = new DeactivationTestRunner(SMSStreamProviderName, GrainClient.Logger);
        }

        public override TestingSiloHost CreateSiloHost()
        {
            var siloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingDeactivationUnitTests.xml"),
            };

            var clientOptions = new TestingClientOptions
            {
                ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
            };

            return new TestingSiloHost(siloOptions, clientOptions);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest_ClientConsumer()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest_ClientConsumer(Guid.NewGuid(), StreamNamespace);
        }

    }
}
