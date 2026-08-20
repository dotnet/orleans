using Orleans.TestingHost;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;

namespace Tester.ZooKeeperUtils
{
    [TestCategory("Membership"), TestCategory("ZooKeeper")]
    [TestSuite("Functional")]
    [TestProvider("ZooKeeper")]
    [TestArea("Membership")]
    public class LivenessTests_ZK : LivenessTestsBase
    {
        public LivenessTests_ZK(ITestOutputHelper output) : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            ZookeeperTestUtils.EnsureZooKeeper();

            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseZooKeeperClustering(options => { options.ConnectionString = TestDefaultConfiguration.ZooKeeperConnectionString!; });
            }
        }

        [Fact]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
