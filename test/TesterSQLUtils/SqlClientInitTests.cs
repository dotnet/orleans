using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Tests.SqlUtils;
using TestExtensions;
using UnitTests.General;
using Xunit;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Tester.SQLUtils
{
    public class SqlClientInitTests : OrleansTestingBase, IClassFixture<SqlClientInitTests.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            public ClusterConfiguration ClusterConfiguration { get; private set; }

            public ClientConfiguration ClientConfiguration { get; private set; }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                string connectionString = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, "OrleansStatisticsTestSQL")
                            .Result.CurrentConnectionString;
                builder.Options.InitialSilosCount = 1;

                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.MemoryStorage>("MemoryStore");
                    legacy.ClusterConfiguration.Globals.RegisterStatisticsProvider<Orleans.Providers.SqlServer.SqlStatisticsPublisher>(statisticProviderName,
                        new Dictionary<string, string>() {{"ConnectionString", connectionString}});
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsProviderName = statisticProviderName);
                    legacy.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
                    legacy.ClusterConfiguration.Globals.DataConnectionString = connectionString;
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(10));

                    legacy.ClientConfiguration.RegisterStatisticsProvider<Orleans.Providers.SqlServer.SqlStatisticsPublisher>(statisticProviderName,
                        new Dictionary<string, string>() {{"ConnectionString", connectionString}});
                    legacy.ClientConfiguration.GatewayProvider = ClientConfiguration.GatewayProviderType.SqlServer;
                    legacy.ClientConfiguration.DataConnectionString = connectionString;
                    legacy.ClientConfiguration.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(10);
                    this.ClientConfiguration = legacy.ClientConfiguration;
                    this.ClusterConfiguration = legacy.ClusterConfiguration;
                });
            }
        }

        static string statisticProviderName = "SQL";
        private readonly Fixture fixture;
        protected TestCluster HostedCluster => this.fixture.HostedCluster;

        public SqlClientInitTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Client"), TestCategory("Stats"), TestCategory("SqlServer")]
        public async Task ClientInit_SqlServer_WithStats()
        {
            Assert.True(this.HostedCluster.Client.IsInitialized);

            ClientConfiguration config = this.fixture.ClientConfiguration;

            Assert.Equal(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider);  // "GatewayProviderType"

            Assert.True(config.UseSqlSystemStore, "Client UseSqlSystemStore");

            var clientStatisticsManager = this.HostedCluster.ServiceProvider.GetService<ClientStatisticsManager>();
            Assert.NotNull(clientStatisticsManager); // Client Statistics Manager is setup

            SiloHandle silo = this.HostedCluster.Primary;
            Assert.True(await this.HostedCluster.Client.GetTestHooks(silo).HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");
            var nodeConfig = this.fixture.ClusterConfiguration.GetOrCreateNodeConfigurationForSilo(Silo.PrimarySiloName);
            Assert.Equal(statisticProviderName, nodeConfig.StatisticsProviderName);  // "Silo.StatisticsProviderName"
        }
    }
}
