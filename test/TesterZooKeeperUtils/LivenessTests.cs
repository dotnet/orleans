using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System.Threading.Tasks;
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

        public override TestCluster CreateTestCluster()
        {
            ZookeeperTestUtils.EnsureZooKeeper();

            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.ZooKeeperConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
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
