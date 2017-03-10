using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;

namespace Tester.SQLUtils
{
    public class SqlClientInitTests : OrleansTestingBase, IClassFixture<SqlClientInitTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {

                string connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansStatisticsTestSQL")
                            .Result.CurrentConnectionString;
                var options = new TestClusterOptions(initialSilosCount: 1);

                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.MemoryStorage>("MemoryStore");
                options.ClusterConfiguration.Globals.RegisterStatisticsProvider<Orleans.Providers.SqlServer.SqlStatisticsPublisher>(statisticProviderName, new Dictionary<string, string>() { { "ConnectionString", connectionString } });
                options.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsProviderName = statisticProviderName);
                options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
                options.ClusterConfiguration.Globals.DataConnectionString = connectionString;
                options.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(10));

                options.ClientConfiguration.RegisterStatisticsProvider<Orleans.Providers.SqlServer.SqlStatisticsPublisher>(statisticProviderName, new Dictionary<string, string>() { { "ConnectionString", connectionString } });
                options.ClientConfiguration.GatewayProvider = ClientConfiguration.GatewayProviderType.SqlServer;
                options.ClientConfiguration.DataConnectionString = connectionString;
                options.ClientConfiguration.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(10);

                return new TestCluster(options);
            }

        }

        static string statisticProviderName = "SQL";
        protected TestCluster HostedCluster { get; private set; }

        public SqlClientInitTests(Fixture fixture)
        {
            HostedCluster = fixture.HostedCluster;
        }

        [Fact, TestCategory("Client"), TestCategory("Stats"), TestCategory("SqlServer")]
        public async Task ClientInit_SqlServer_WithStats()
        {
            Assert.True(this.HostedCluster.Client.IsInitialized);

            ClientConfiguration config = this.HostedCluster.ClientConfiguration;

            Assert.Equal(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider);  // "GatewayProviderType"

            Assert.True(config.UseSqlSystemStore, "Client UseSqlSystemStore");

            var clientStatisticsManager = this.HostedCluster.ServiceProvider.GetService<ClientStatisticsManager>();
            Assert.NotNull(clientStatisticsManager); // Client Statistics Manager is setup

            Assert.Equal(statisticProviderName, config.StatisticsProviderName);  // "Client.StatisticsProviderName"

            SiloHandle silo = this.HostedCluster.Primary;
            Assert.True(await this.HostedCluster.Client.GetTestHooks(silo).HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");
            Assert.Equal(statisticProviderName, silo.NodeConfiguration.StatisticsProviderName);  // "Silo.StatisticsProviderName"
        }
    }
}
