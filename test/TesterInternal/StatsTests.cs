using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Microsoft.Extensions.Options;

namespace UnitTests.Stats
{
    public class StatsInitTests : OrleansTestingBase, IClassFixture<StatsInitTests.Fixture>
    {
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;

                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterStatisticsProvider<UnitTests.Stats.MockStatsSiloCollector>("MockStats");
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(1));
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsLogWriteInterval = TimeSpan.FromSeconds(1));

                    legacy.ClientConfiguration.RegisterStatisticsProvider<UnitTests.Stats.MockStatsClientCollector>("MockStats");
                    legacy.ClientConfiguration.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(1);
                    legacy.ClientConfiguration.StatisticsLogWriteInterval = TimeSpan.FromSeconds(1);
                });
            }
        }

        protected TestCluster HostedCluster { get; private set; }

        public StatsInitTests(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
            this.HostedCluster = fixture.HostedCluster;
        }

        [Fact, TestCategory("Functional"), TestCategory("Client"), TestCategory("Stats")]
        public async Task Stats_Init_Mock()
        {
            var clientStatisticsManager = this.HostedCluster.ServiceProvider.GetService<ClientStatisticsManager>();
            Assert.NotNull(clientStatisticsManager); // Client Statistics Manager is setup

            SiloHandle silo = this.HostedCluster.Primary;
            Assert.True(await this.HostedCluster.Client.GetTestHooks(silo).HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");

            // Check we got some stats & metrics callbacks on both client and server.
            var siloStatsCollector = this.fixture.GrainFactory.GetGrain<IStatsCollectorGrain>(0);
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
            StatisticsCollector.Initialize(StatisticsLevel.Info);
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
}
