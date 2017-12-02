using System;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using System.Threading.Tasks;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost.Utils;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace Consul.Tests
{
    [TestCategory("Membership"), TestCategory("Consul")]
    public class LivenessTests_Consul : LivenessTestsBase
    {
        public LivenessTests_Consul(ITestOutputHelper output) : base(output)
        {
        }

        public override TestCluster CreateTestCluster()
        {
            ConsulTestUtils.EnsureConsul();

            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = ConsulTestUtils.CONSUL_ENDPOINT;
            options.ClusterConfiguration.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.Disabled;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options).UseSiloBuilderFactory<SiloBuilderFactory>()
                .UseClientBuilderFactory(clientBuilderFactory);
        }

        private Func<ClientConfiguration, IClientBuilder> clientBuilderFactory = config => new ClientBuilder()
            .UseConfiguration(config).UseConsulGatewayListProvider(gatewayOptions =>
            {
                gatewayOptions.Address = new Uri(ConsulTestUtils.CONSUL_ENDPOINT);;
            })
            .ConfigureApplicationParts(parts => parts.AddFromAppDomain())
            .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(config.ClientName, config.ClusterId)));

        public class SiloBuilderFactory : ISiloBuilderFactory
        {
            public ISiloHostBuilder CreateSiloBuilder(string siloName, ClusterConfiguration clusterConfiguration)
            {
                return new SiloHostBuilder()
                    .ConfigureSiloName(siloName)
                    .UseConfiguration(clusterConfiguration)
                    .UseConsulMembership(options =>
                    {
                        options.Address = new Uri(ConsulTestUtils.CONSUL_ENDPOINT);
                    })
                    .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, TestingUtils.CreateTraceFileName(siloName, clusterConfiguration.Globals.ClusterId)));
            }
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Consul_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Consul_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Consul_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Consul_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_Consul_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
