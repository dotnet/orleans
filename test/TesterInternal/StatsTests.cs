using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
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
        public async Task Stats_Init_Mock()
        {
            ClientConfiguration config = this.HostedCluster.ClientConfig;

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.NotNull(ogc.ClientStatistics);

            Assert.Equal("MockStats",  config.StatisticsProviderName);  // "Client.StatisticsProviderName"

            SiloHandle silo = this.HostedCluster.Primary;
            Assert.True(await silo.TestHook.HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");

            // Check we got some stats & metrics callbacks on both client and server.
            var siloStatsCollector = GrainFactory.GetGrain<IStatsCollectorGrain>(0);
            var clientStatsCollector = MockStatsCollectorClient.StatsPublisherInstance;
            var clientMetricsCollector = MockStatsCollectorClient.MetricsPublisherInstance;

            // Stats publishing is set to 1s interval in config files.
            await Task.Delay(TimeSpan.FromSeconds(3));

            long numClientStatsCalls = clientStatsCollector.NumStatsCalls;
            long numClientMetricsCalls = clientMetricsCollector.NumMetricsCalls;
            long numSiloStatsCalls = await siloStatsCollector.GetReportStatsCallCount();
            long numSiloMetricsCalls = await siloStatsCollector.GetReportMetricsCallCount();
            output.WriteLine("Client - Metrics calls = {0} Stats calls = {1}", numClientMetricsCalls,
                numClientMetricsCalls);
            output.WriteLine("Silo - Metrics calls = {0} Stats calls = {1}", numSiloStatsCalls, numSiloStatsCalls);

            Assert.True(numClientMetricsCalls > 0, $"Some client metrics calls = {numClientMetricsCalls}");
            Assert.True(numSiloMetricsCalls > 0, $"Some silo metrics calls = {numSiloMetricsCalls}");
            Assert.True(numClientStatsCalls > 0, $"Some client stats calls = {numClientStatsCalls}");
            Assert.True(numSiloStatsCalls > 0, $"Some silo stats calls = {numSiloStatsCalls}");
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
        public async Task ClientInit_SqlServer_WithStats()
        {
            Assert.True(GrainClient.IsInitialized);

            ClientConfiguration config = this.HostedCluster.ClientConfig;

            Assert.Equal(ClientConfiguration.GatewayProviderType.SqlServer,  config.GatewayProvider);  // "GatewayProviderType"

            Assert.True(config.UseSqlSystemStore, "Client UseSqlSystemStore");

            OutsideRuntimeClient ogc = (OutsideRuntimeClient) RuntimeClient.Current;
            Assert.NotNull(ogc.ClientStatistics); // Client Statistics Manager is setup

            Assert.Equal("SQL",  config.StatisticsProviderName);  // "Client.StatisticsProviderName"

            SiloHandle silo = this.HostedCluster.Primary;
            Assert.True(await silo.TestHook.HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");
            Assert.Equal("SQL",  silo.NodeConfiguration.StatisticsProviderName);  // "Silo.StatisticsProviderName"
        }
    }
#endif
}
