using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils
{
    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        public LivenessTests_AzureTable(ITestOutputHelper output) : base(output)
        {
            TestUtils.CheckForAzureStorage();
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
