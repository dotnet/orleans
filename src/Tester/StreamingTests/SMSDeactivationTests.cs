using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.StreamingTests
{
    [DeploymentItem("OrleansConfigurationForStreamingDeactivationUnitTests.xml")]
    [DeploymentItem("ClientConfigurationForStreamTesting.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class SMSDeactivationTests : UnitTestSiloHost
    {
        private const string SMSStreamProviderName = "SMSProvider";
        private const string StreamNamespace = "SMSDeactivationTestsNamespace";
        private readonly DeactivationTestRunner runner;

        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartSecondary = false,
            SiloConfigFile = new FileInfo("OrleansConfigurationForStreamingDeactivationUnitTests.xml"),
        };
        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("ClientConfigurationForStreamTesting.xml")
        };

        public SMSDeactivationTests()
            : base(siloOptions, clientOptions)
        {
            runner = new DeactivationTestRunner(SMSStreamProviderName, GrainClient.Logger);
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest(Guid.NewGuid(), StreamNamespace);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMSDeactivationTest_ClientConsumer()
        {
            logger.Info("************************ SMSDeactivationTest *********************************");
            await runner.DeactivationTest_ClientConsumer(Guid.NewGuid(), StreamNamespace);
        }

    }
}
