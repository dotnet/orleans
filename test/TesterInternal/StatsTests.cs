using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Xunit;
using Tester;
using UnitTests.Tester;
using Xunit.Abstractions;

namespace UnitTests.Stats
{
    public class StatsInitTests : OrleansTestingBase, IClassFixture<StatsInitTests.Fixture>
    {
        private readonly ITestOutputHelper output;

        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = false,
                    SiloConfigFile = new FileInfo("MockStats_ServerConfiguration.xml"),
                    ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
                }, new TestingClientOptions
                {
                    ClientConfigFile = new FileInfo("MockStats_ClientConfiguration.xml")
                });
            }
        }

        protected TestingSiloHost HostedCluster { get; private set; }

        public StatsInitTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            HostedCluster = fixture.HostedCluster;
        }

        [Fact, TestCategory("Functional"), TestCategory("Client"), TestCategory("Stats")]
        public void Stats_Init_Mock()
        {
            ClientConfiguration config = this.HostedCluster.ClientConfig;

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.IsNotNull(ogc.ClientStatistics, "Client Statistics Manager is setup");

            Assert.AreEqual("MockStats", config.StatisticsProviderName, "Client.StatisticsProviderName");

            Silo silo = this.HostedCluster.Primary.Silo;
            Assert.IsTrue(silo.TestHook.HasStatisticsProvider, "Silo StatisticsProviderManager is setup");
            Assert.AreEqual("MockStats", silo.LocalConfig.StatisticsProviderName, "Silo.StatisticsProviderName");

            // Check we got some stats & metrics callbacks on both client and server.
            var siloStatsCollector = this.HostedCluster.Primary.Silo.TestHook.StatisticsProvider as MockStatsSiloCollector;
            var clientStatsCollector = MockStatsCollectorClient.StatsPublisherInstance;
            var clientMetricsCollector = MockStatsCollectorClient.MetricsPublisherInstance;

            // Stats publishing is set to 1s interval in config files.
            Thread.Sleep(TimeSpan.FromSeconds(2));

            long numClientStatsCalls = clientStatsCollector.NumStatsCalls;
            long numClientMetricsCalls = clientMetricsCollector.NumMetricsCalls;
            long numSiloStatsCalls = siloStatsCollector.NumStatsCalls;
            long numSiloMetricsCalls = siloStatsCollector.NumMetricsCalls;
            output.WriteLine("Client - Metrics calls = {0} Stats calls = {1}", numClientMetricsCalls,
                numSiloMetricsCalls);
            output.WriteLine("Silo - Metrics calls = {0} Stats calls = {1}", numClientStatsCalls, numSiloStatsCalls);

            Assert.IsTrue(numClientMetricsCalls > 0, "Some client metrics calls = {0}", numClientMetricsCalls);
            Assert.IsTrue(numSiloMetricsCalls > 0, "Some silo metrics calls = {0}", numSiloMetricsCalls);
            Assert.IsTrue(numClientStatsCalls > 0, "Some client stats calls = {0}", numClientStatsCalls);
            Assert.IsTrue(numSiloStatsCalls > 0, "Some silo stats calls = {0}", numSiloStatsCalls);
        }
    }
    
    public class StatsTestsNoSilo
    {
        private readonly ITestOutputHelper output;

        public StatsTestsNoSilo(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Manual"), TestCategory("Stats")]
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
                        //    output.WriteLine("Thread {0}: {1}% done", capture, i * 100 / nIterations);
                    }
                }));
            }

            Task.WhenAll(tasks).Wait();
            
            sw.Stop();
            output.WriteLine("Done. "+ sw.ElapsedMilliseconds);
        }
    }

#if DEBUG || USE_SQL_SERVER

    public class SqlClientInitTests : OrleansTestingBase, IClassFixture<SqlClientInitTests.Fixture>, IDisposable
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = false,
                    SiloConfigFile = new FileInfo("DevTestServerConfiguration.xml"),
                    ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain
                }, new TestingClientOptions
                {
                    ClientConfigFile = new FileInfo("DevTestClientConfiguration.xml")
                });
            }
        }

        protected TestingSiloHost HostedCluster { get; private set; }

        public SqlClientInitTests(Fixture fixture)
        {
            HostedCluster = fixture.HostedCluster;
        }
        
        public void Dispose()
        {
            //output.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            // ResetAllAdditionalRuntimes();
            HostedCluster.StopAdditionalSilos();
        }

        [Fact, TestCategory("Client"), TestCategory("Stats"), TestCategory("SqlServer")]
        public void ClientInit_SqlServer_WithStats()
        {
            Assert.IsTrue(GrainClient.IsInitialized);

            ClientConfiguration config = this.HostedCluster.ClientConfig;

            Assert.AreEqual(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider, "GatewayProviderType");

            Assert.IsTrue(config.UseSqlSystemStore, "Client UseSqlSystemStore");

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.IsNotNull(ogc.ClientStatistics, "Client Statistics Manager is setup");

            Assert.AreEqual("SQL", config.StatisticsProviderName, "Client.StatisticsProviderName");

            Silo silo = this.HostedCluster.Primary.Silo;
            Assert.IsTrue(silo.TestHook.HasStatisticsProvider, "Silo StatisticsProviderManager is setup");
            Assert.AreEqual("SQL", silo.LocalConfig.StatisticsProviderName, "Silo.StatisticsProviderName");
        }
    }
#endif
}
