using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Tester.AzureUtils
{
    [TestCategory("Membership"), TestCategory("AzureStorage")]
    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        public LivenessTests_AzureTable(ITestOutputHelper output) : base(output)
        {
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            TestUtils.CheckForAzureStorage();
            builder.Options.UseTestClusterMembership = false;
            builder.AddSiloBuilderConfigurator<Configurator>();
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        public class Configurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseAzureStorageClustering(options => options.ConfigureTestDefaults());
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseAzureStorageClustering(options => options.ConfigureTestDefaults());
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
