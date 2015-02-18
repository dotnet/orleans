using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;

namespace LoadTest
{
    [TestClass]
    [DeploymentItem("TestConfiguration", "TestConfiguration")] // copy TestConfiguration directory to output directory of same name
    public class LoadTest_Jbragg : LoadTestBase
    {
        [TestInitialize]
        public void Prologue()
        {
            BasePrologue();
        }

        [TestCleanup]
        public void Epilogue()
        {
            BaseEpilogue();
        }

        [TestMethod, TestCategory("jacobs")]
        public void JacobsRampUpMultipleBatchSingleEventBenchmark()
        {
            MockStreamProviderParameters parameters = new MockStreamProviderParameters();
            parameters.TotalQueueCount = 10;
            parameters.NumStreamsPerQueue = 71;
            parameters.MessageProducer = "ImplicitConsumer";
            parameters.ActivationTaskDelay = (int)TimeSpan.FromMilliseconds(200).TotalMilliseconds;
            parameters.ActivationBusyWait = 0;
            parameters.AdditionalSubscribersCount = 8;
            parameters.EventTaskDelay = (int)TimeSpan.FromMilliseconds(20).TotalMilliseconds;
            parameters.EventBusyWait = 0;
            parameters.SiloStabilizationTime = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            parameters.RampUpStagger = (int)(TimeSpan.FromMinutes(7).TotalMilliseconds / parameters.NumStreamsPerQueue);
            parameters.SubscriptionLength = (int)TimeSpan.FromMinutes(7).TotalMilliseconds;
            parameters.StreamEventsPerSecond = 3;
            parameters.MaxBatchesPerRequest = 400;
            parameters.TargetBatchesSentPerSecond = 250;
            parameters.MaxEventsPerBatch = 1;
            parameters.EventSize = 50;
            parameters.CacheSizeKb = 512;
            parameters.StreamProvider = "MockStreamProvider";

            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestLoadScenario(
                "nightly_build",
                "17xcg10_cluster",
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = 10,
                    ClientCount = 1,
                    ClientAppName = "StreamPullingAgentBenchmark",
                    AdditionalParameters =
                        new[]
                        {
                            "--test-name", "StreamPullingAgentBenchmark",
                            "--verbose",
                            "--polling-period", ((int)TimeSpan.FromSeconds(30).TotalSeconds).ToString(CultureInfo.InvariantCulture), 
                            "--test-length", ((int)TimeSpan.FromMinutes(15).TotalSeconds).ToString(CultureInfo.InvariantCulture), 
                            "--warm-up", ((int)TimeSpan.FromMinutes(15).TotalSeconds).ToString(CultureInfo.InvariantCulture)
                        }
                },
                clientGrammar: "ClientGrammerForNoTPSTracking",
                siloOptions: new SiloOptions
                {
                    MockStreamProviderParameters = parameters
                });
        }
    }
}
