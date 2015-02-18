using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;

// ReSharper disable UnusedVariable

namespace LoadTest
{
    [TestClass]
    [DeploymentItem("TestConfiguration", "TestConfiguration")] // copy TestConfiguration directory to output directory of same name
    public class LoadTest_Streaming : LoadTestBase
    {
        private const string CLUSTER_NAME = "17xcg18_cluster";

        public LoadTest_Streaming()
        {
        }

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


        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        private static int StreamingTestLength(TimeSpan executionTime, int expectedPerClientTPS)
        {
            // this needs to be a nice round number or the load test framework will throw an exception because parameterized numbers are indivisible.
            return (int)executionTime.TotalSeconds * expectedPerClientTPS;
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("SMS")]
        public void SMS_StreamingBenchmark()
        {
            TestLoadScenario(
                "nightly_build",
                CLUSTER_NAME,
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = 1,
                    ClientCount = 8,
                    ClientAppName = "SMSStreamingBenchmark",
                    Number = StreamingTestLength(TimeSpan.FromMinutes(20), 3000),
                    Threads = 8,
                    Workers = 1,
                    Pipeline = 20*1000
                },
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        private static readonly TimeSpan rollingBenckmarkDuration = 
#if DEBUG
            TimeSpan.FromMinutes(2);
#else
            TimeSpan.FromMinutes(10);
#endif

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("SMS"), TestCategory("PubSub")]
        public void SMS_Subscribe_P0_Rolling_Benchmark()
        {
            SMS_PubSub_Test_Impl(0, unSubscribe: false, shareStreams: false);
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("SMS"), TestCategory("PubSub")]
        public void SMS_Subscribe_P1_Rolling_Benchmark()
        {
            SMS_PubSub_Test_Impl(1, unSubscribe: false, shareStreams: false);
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("SMS"), TestCategory("PubSub")]
        public void SMS_SubUnsub_P1_Rolling_Benchmark()
        {
            SMS_PubSub_Test_Impl(1, unSubscribe: true, shareStreams: false);
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("SMS"), TestCategory("PubSub")]
        public void SMS_SubUnsub_P1_Rolling_Benchmark_ShareStreams()
        {
            SMS_PubSub_Test_Impl(1, unSubscribe: true, shareStreams: true);
        }

        private void SMS_PubSub_Test_Impl(int numProducersPerStream, bool unSubscribe, bool shareStreams)
        {
            int numConsumersPerStream = 10;
            int numStreams = 1000;
            int pipelineSize = 1000;
            int numWorkersPerClient = 1;
            int numThreadsPerClient = 5;
            int numServerMachines = 10;
            int numClientMachines = 10;

            TestLoadScenario(
                "nightly_build",
                CLUSTER_NAME,
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = numServerMachines,
                    ClientCount = numClientMachines,
                    ClientAppName = "SMSPubSubSubscribeBenchmark",
                    AdditionalParameters =
                        new[]
                        {
                            "-streamCount", numStreams.ToString(CultureInfo.InvariantCulture),
                            "-publishersPerStream", numProducersPerStream.ToString(CultureInfo.InvariantCulture), 
                            "-consumersPerStream", numConsumersPerStream.ToString(CultureInfo.InvariantCulture),
                            "-unsub", unSubscribe.ToString(CultureInfo.InvariantCulture),
                            "-shareStream", shareStreams.ToString(CultureInfo.InvariantCulture)
                        },
                    Number = StreamingTestLength(rollingBenckmarkDuration, 200),
                    Threads = numThreadsPerClient,
                    Workers = numWorkersPerClient,
                    Pipeline = pipelineSize
                },
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("PullingAgent")]
        public void StreamPullingAgentBenchmark_Basic()
        {
            TestLoadScenario(
                "nightly_build",
                CLUSTER_NAME,
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = 8,
                    ClientCount = 1,
                    ClientAppName = "StreamPullingAgentBenchmark",
                    AdditionalParameters =
                        new[]
                        {
                            "--test-name", "StreamPullingAgentBenchmark",
                            "--verbose", 
                            "--polling-period", 30.ToString(CultureInfo.InvariantCulture), 
                            "--test-length", (30 * 60).ToString(CultureInfo.InvariantCulture), 
                            "--warm-up", (3 * 60).ToString(CultureInfo.InvariantCulture)
                        }
                },
                clientGrammar: "ClientGrammerForNoTPSTracking",
                siloOptions: new SiloOptions { MockStreamProviderParameters = new MockStreamProviderParameters() });
        }

        [TestMethod, TestCategory("StreamingPerformance"), TestCategory("PullingAgent")]
        public void SubscriptionBurstBenchmark()
        {
            TestLoadScenario(
                "nightly_build",
                CLUSTER_NAME,
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = 8,
                    ClientCount = 1,
                    ClientAppName = "StreamPullingAgentBenchmark",
                    AdditionalParameters =
                        new[]
                        {
                            "--test-name", "StreamPullingAgentBenchmark",
                            "--verbose", 
                            "--polling-period", 30.ToString(CultureInfo.InvariantCulture), 
                            "--test-length", (30 * 60).ToString(CultureInfo.InvariantCulture), 
                            "--warm-up", (3 * 60).ToString(CultureInfo.InvariantCulture)
                        }
                },
                clientGrammar: "ClientGrammerForNoTPSTracking",
                siloOptions: new SiloOptions
                {
                    MockStreamProviderParameters = new MockStreamProviderParameters
                    {
                        NumStreamsPerQueue = 100,
                        MessageProducer = "ImplicitConsumer",
                        ActivationTaskDelay = 200,
                        ActivationBusyWait = 0,
                        EventTaskDelay = 20,
                        EventBusyWait = 0
                    }
                });
        }
    }
}
// ReSharper restore UnusedVariable
