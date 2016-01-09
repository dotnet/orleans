using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using UnitTests.Tester;

namespace UnitTests.Streaming
{
    [DeploymentItem("Config_AzureStreamProviders.xml")]
    [DeploymentItem("ClientConfig_AzureStreamProviders.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    [TestClass]
    public class AQStreamingTests : UnitTestSiloHost
    {
        internal static readonly FileInfo SiloConfigFile = new FileInfo("Config_AzureStreamProviders.xml");
        internal static readonly FileInfo ClientConfigFile = new FileInfo("ClientConfig_AzureStreamProviders.xml");

        private static readonly TestingSiloOptions aqSiloOptions = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = SiloConfigFile,
            };
        private static readonly TestingClientOptions aqClientOptions = new TestingClientOptions
            {
                ClientConfigFile = ClientConfigFile
            };
        private static readonly TestingSiloOptions aqSiloOption_OnlyPrimary = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = SiloConfigFile,
                StartSecondary = false,
            };

        private static readonly TestingSiloOptions aqSiloOption_NoClient = new TestingSiloOptions
            {
                StartFreshOrleans = true,
                SiloConfigFile = SiloConfigFile,
                StartClient = false
            };

        private readonly SingleStreamTestRunner runner;

        public AQStreamingTests()
            : base(aqSiloOptions, aqClientOptions)
        {
            runner = new SingleStreamTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }

        public AQStreamingTests(int dummy)
            : base(aqSiloOption_OnlyPrimary, aqClientOptions)
        {
            runner = new SingleStreamTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }

        public AQStreamingTests(string dummy)
            : base(aqSiloOption_NoClient)
        {
            runner = new SingleStreamTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            //ResetAllAdditionalRuntimes();
            //ResetDefaultRuntimes();
            StopAllSilos();
        }

        //[TestInitialize]
        //public void TestInitialize()
        //{
        //    //DeleteAllQueues();
        //}

        [TestCleanup]
        public void TestCleanup()
        {
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, DeploymentId, StorageTestConstants.DataConnectionString, logger).Wait();
        }

        ////------------------------ One to One ----------------------//

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                StartAdditionalSilo);
        }

        //[TestMethod, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_18_MultipleStreams_1J_1F_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 18, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                StartAdditionalSilo,
                StopSilo);
        }

        [TestMethod, TestCategory("Streaming")]
        public async Task AQ_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [TestMethod, TestCategory("Streaming")]
        public async Task AQ_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [TestMethod, TestCategory("Streaming"), TestCategory("Failures")]
        [Ignore]
        public async Task AQ_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }
    }
}