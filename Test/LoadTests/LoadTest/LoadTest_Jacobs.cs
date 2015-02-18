using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;

namespace LoadTest
{
    [TestClass]
    [DeploymentItem("TestConfiguration", "TestConfiguration")] // copy TestConfiguration directory to output directory of same name
    public class LoadTest_Jacobs : LoadTestBase
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
        public void RampUpMultipleBatchSingleEventBenchmarkamingBenchmark()
        {
            MockStreamProviderParameters parameters = new MockStreamProviderParameters();
            parameters.TotalQueueCount = 100;
            parameters.NumStreamsPerQueue = 100;
            parameters.MessageProducer = "ImplicitConsumer";
            parameters.ActivationTaskDelay = (int)TimeSpan.FromMilliseconds(200).TotalMilliseconds;
            parameters.ActivationBusyWait = 0;
            parameters.AdditionalSubscribersCount = 8;
            parameters.EventTaskDelay = (int)TimeSpan.FromMilliseconds(20).TotalMilliseconds;
            parameters.EventBusyWait = 0;
            parameters.SiloStabilizationTime = (int)TimeSpan.FromMinutes(1).TotalMilliseconds;
            parameters.RampUpStagger = (int)(TimeSpan.FromMinutes(7).TotalMilliseconds / parameters.NumStreamsPerQueue);
            parameters.SubscriptionLength = (int)TimeSpan.FromHours(3).TotalMilliseconds;
            parameters.StreamEventsPerSecond = 1;
            parameters.MaxBatchesPerRequest = parameters.NumStreamsPerQueue;
            parameters.MaxEventsPerBatch = 1;
            parameters.EventSize = 50;

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

        [TestMethod, TestCategory("jacobs")]
        public void JacobsRemindersBenchmark()
        {
            const int siloCount = 10;

            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestLoadScenario(
                "nightly_build",
                "17xcg10_cluster",
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = siloCount,
                    ClientCount = 1,
                    ClientAppName = "StreamPullingAgentBenchmark",
                    AdditionalParameters =
                        new[]
                        {
                            "--verbose",
                            "--testname", "newreminderloadtest",
                            "--polling-period", ((int)TimeSpan.FromSeconds(30).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            "--warm-up", ((int)TimeSpan.FromMinutes(10).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            "--test-length", ((int)TimeSpan.FromMinutes(15).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            "--reminders-per-second", (15 * siloCount).ToString(CultureInfo.InvariantCulture),
                            "--reminder-period", ((int)TimeSpan.FromMinutes(5).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            "--reminder-duration", ((int)TimeSpan.FromMinutes(7).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                            "--concurrent-requests", (4 * siloCount).ToString(CultureInfo.InvariantCulture),
                            "--skip-get", (true).ToString(CultureInfo.InvariantCulture)
                        }
                },
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }
    }
}
