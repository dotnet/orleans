using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System.Threading.Tasks;
using Orleans.Hosting;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.ZooKeeperUtils
{
    public class LivenessTests_ZK : LivenessTestsBase
    {
        public LivenessTests_ZK(ITestOutputHelper output) : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            ZookeeperTestUtils.EnsureZooKeeper();

            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.ZooKeeperConnectionString;
                legacy.ClusterConfiguration.PrimaryNode = null;
                legacy.ClusterConfiguration.Globals.SeedNodes.Clear();
                legacy.ClientConfiguration.GatewayProvider = ClientConfiguration.GatewayProviderType.ZooKeeper;
            });
        }

        public class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.UseZooKeeperClustering(options => { options.ConnectionString = TestDefaultConfiguration.DataConnectionString; });
            }
        }

        [SkippableFact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
