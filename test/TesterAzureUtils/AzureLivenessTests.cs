using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.AzureUtils.Configuration;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using OrleansAzureUtils;
using TestExtensions;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils
{
    [TestCategory("Membership"), TestCategory("Azure")]
    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        public LivenessTests_AzureTable(ITestOutputHelper output) : base(output)
        {
        }

        public override TestCluster CreateTestCluster()
        {
            TestUtils.CheckForAzureStorage();
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options).UseSiloBuilderFactory<SiloBuilderFactory>();
        }

        public class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseAzureMembershipTableFromLegacyConfigurationSupport()
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, clusterConfiguration.GetOrCreateNodeConfigurationForSilo(siloName).TraceFileName));
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
