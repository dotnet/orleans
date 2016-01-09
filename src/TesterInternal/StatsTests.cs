using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests.Stats
{
    [TestClass]
    [DeploymentItem("MockStats_ClientConfiguration.xml")]
    [DeploymentItem("MockStats_ServerConfiguration.xml")]
    public class StatsInitTests : UnitTestSiloHost
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
            SiloConfigFile = new FileInfo("MockStats_ServerConfiguration.xml"),
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("MockStats_ClientConfiguration.xml")
        };

        public StatsInitTests()
            : base(siloOptions, clientOptions)
        {
        }

        [TestCleanup]
        public void TestCleanup()
        {
            StopAdditionalSilos();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Client"), TestCategory("Stats")]
        public void Stats_Init_Mock()
        {
            ClientConfiguration config = this.ClientConfig;

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.IsNotNull(ogc.ClientStatistics, "Client Statistics Manager is setup");

            Assert.AreEqual("MockStats", config.StatisticsProviderName, "Client.StatisticsProviderName");

            Silo silo = Primary.Silo;
            Assert.IsTrue(silo.TestHook.HasStatisticsProvider, "Silo StatisticsProviderManager is setup");
            Assert.AreEqual("MockStats", silo.LocalConfig.StatisticsProviderName, "Silo.StatisticsProviderName");

            // Check we got some stats & metrics callbacks on both client and server.
            var siloStatsCollector = Primary.Silo.TestHook.StatisticsProvider as MockStatsSiloCollector;
            var clientStatsCollector = MockStatsCollectorClient.StatsPublisherInstance;
            var clientMetricsCollector = MockStatsCollectorClient.MetricsPublisherInstance;

            // Stats publishing is set to 1s interval in config files.
            Thread.Sleep(TimeSpan.FromSeconds(2));

            long numClientStatsCalls = clientStatsCollector.NumStatsCalls;
            long numClientMetricsCalls = clientMetricsCollector.NumMetricsCalls;
            long numSiloStatsCalls = siloStatsCollector.NumStatsCalls;
            long numSiloMetricsCalls = siloStatsCollector.NumMetricsCalls;
            Console.WriteLine("Client - Metrics calls = {0} Stats calls = {1}", numClientMetricsCalls,
                numSiloMetricsCalls);
            Console.WriteLine("Silo - Metrics calls = {0} Stats calls = {1}", numClientStatsCalls, numSiloStatsCalls);

            Assert.IsTrue(numClientMetricsCalls > 0, "Some client metrics calls = {0}", numClientMetricsCalls);
            Assert.IsTrue(numSiloMetricsCalls > 0, "Some silo metrics calls = {0}", numSiloMetricsCalls);
            Assert.IsTrue(numClientStatsCalls > 0, "Some client stats calls = {0}", numClientStatsCalls);
            Assert.IsTrue(numSiloStatsCalls > 0, "Some silo stats calls = {0}", numSiloStatsCalls);
        }
    }

    [TestClass]
    public class StatsTestsNoSilo
    {
        [TestMethod, TestCategory("Manual"), TestCategory("Stats")]
        public void ApplicationRequestsStatisticsGroup_Perf()
        {
            var config = new NodeConfiguration();
            config.StatisticsCollectionLevel = StatisticsLevel.Info;
            StatisticsCollector.Initialize(config);
            ApplicationRequestsStatisticsGroup.Init(TimeSpan.FromSeconds(30));
            const long nIterations = 10000000;
            const int nValues = 1000;
            var rand = new Random();
            var times = new TimeSpan[nValues];

            for (int i = 0; i < 1000; i++)
            {
                times[i] = TimeSpan.FromMilliseconds(rand.Next(30000));
            }


            Stopwatch sw = Stopwatch.StartNew();

            var tasks = new List<Task>();
            for (int j = 0; j < 10; j++)
            {
                //int capture = j;

                tasks.Add(Task.Run(() =>
                {
                    //long tenPercent = nIterations/10;

                    for (long i = 0; i < nIterations; i++)
                    {
                        ApplicationRequestsStatisticsGroup.OnAppRequestsEnd(times[i % nValues]);
                        //if (i % tenPercent == 0)
                        //    Console.WriteLine("Thread {0}: {1}% done", capture, i * 100 / nIterations);
                    }
                }));
            }

            Task.WhenAll(tasks).Wait();
            
            sw.Stop();
            Console.WriteLine("Done.");
            Console.WriteLine(sw.ElapsedMilliseconds);
        }
    }

#if DEBUG || USE_SQL_SERVER

    [TestClass]
    [DeploymentItem("DevTestClientConfiguration.xml")]
    [DeploymentItem("DevTestServerConfiguration.xml")]
    [DeploymentItem("OrleansProviders.dll")]
    public class SqlClientInitTests : UnitTestSiloHost
    {
        private static readonly TestingSiloOptions siloOptions = new TestingSiloOptions
        {
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
            SiloConfigFile = new FileInfo("DevTestServerConfiguration.xml"),
            ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
        };

        private static readonly TestingClientOptions clientOptions = new TestingClientOptions
        {
            ClientConfigFile = new FileInfo("DevTestClientConfiguration.xml")
        };

        public SqlClientInitTests()
            : base(siloOptions, clientOptions)
        {
        }

        [TestCleanup]
        public void TestCleanup()
        {
            //Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
           // ResetAllAdditionalRuntimes();
		   StopAdditionalSilos();
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            //ResetAllAdditionalRuntimes();
            //ResetDefaultRuntimes();
            StopAllSilos();
        }

        [TestMethod, TestCategory("Client"), TestCategory("Stats"), TestCategory("SqlServer")]
        public void ClientInit_SqlServer_WithStats()
        {
            Assert.IsTrue(GrainClient.IsInitialized);

            ClientConfiguration config = ClientConfig;

            Assert.AreEqual(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider, "GatewayProviderType");

            Assert.IsTrue(config.UseSqlSystemStore, "Client UseSqlSystemStore");

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.IsNotNull(ogc.ClientStatistics, "Client Statistics Manager is setup");

            Assert.AreEqual("SQL", config.StatisticsProviderName, "Client.StatisticsProviderName");

            Silo silo = Primary.Silo;
            Assert.IsTrue(silo.TestHook.HasStatisticsProvider, "Silo StatisticsProviderManager is setup");
            Assert.AreEqual("SQL", silo.LocalConfig.StatisticsProviderName, "Silo.StatisticsProviderName");
        }
    }
#endif
}
