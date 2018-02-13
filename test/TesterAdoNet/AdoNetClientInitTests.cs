using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Linq;
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
    public class AdoNetClientInitTests : OrleansTestingBase, IClassFixture<AdoNetClientInitTests.Fixture>
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
                    legacy.ClusterConfiguration.Globals.RegisterStatisticsProvider<Orleans.Providers.AdoNet.AdoNetStatisticsPublisher>(statisticProviderName,
                        new Dictionary<string, string>() {{"ConnectionString", connectionString}});
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsProviderName = statisticProviderName);
                    legacy.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AdoNet;
                    legacy.ClusterConfiguration.Globals.DataConnectionString = connectionString;
                    legacy.ClusterConfiguration.ApplyToAllNodes(nc => nc.StatisticsMetricsTableWriteInterval = TimeSpan.FromSeconds(10));

                    legacy.ClientConfiguration.RegisterStatisticsProvider<Orleans.Providers.AdoNet.AdoNetStatisticsPublisher>(statisticProviderName,
                        new Dictionary<string, string>() {{"ConnectionString", connectionString}});
                    legacy.ClientConfiguration.GatewayProvider = ClientConfiguration.GatewayProviderType.AdoNet;
                    legacy.ClientConfiguration.DataConnectionString = connectionString;
                    this.ClientConfiguration = legacy.ClientConfiguration;
                    this.ClusterConfiguration = legacy.ClusterConfiguration;
                });
            }
        }

        static string statisticProviderName = "SQL";
        private readonly Fixture fixture;
        protected TestCluster HostedCluster => this.fixture.HostedCluster;

        public AdoNetClientInitTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Client"), TestCategory("Stats"), TestCategory("AdoNet")]
        public async Task ClientInit_AdoNet_WithStats()
        {
            Assert.True(this.HostedCluster.Client.IsInitialized);

            ClientConfiguration config = this.fixture.ClientConfiguration;

            Assert.Equal(ClientConfiguration.GatewayProviderType.AdoNet, config.GatewayProvider);  // "GatewayProviderType"

            Assert.True(config.UseAdoNetSystemStore, "Client UseAdoNetSystemStore");

            var clientStatisticsManager = this.HostedCluster.ServiceProvider.GetService<ClientStatisticsManager>();
            Assert.NotNull(clientStatisticsManager); // Client Statistics Manager is setup

            SiloHandle silo = this.HostedCluster.Silos.First();
            Assert.True(await this.HostedCluster.Client.GetTestHooks(silo).HasStatisticsProvider(), "Silo StatisticsProviderManager is setup");
            var nodeConfig = this.fixture.ClusterConfiguration.GetOrCreateNodeConfigurationForSilo(Silo.PrimarySiloName);
            Assert.Equal(statisticProviderName, nodeConfig.StatisticsProviderName);  // "Silo.StatisticsProviderName"
        }
    }
}
