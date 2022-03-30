using System;
using Orleans.TestingHost;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Hosting;
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

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            ConsulTestUtils.EnsureConsul();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseConsulSiloClustering(options =>
                {
                    var address = new Uri(ConsulTestUtils.CONSUL_ENDPOINT);
                    options.ConfigureConsulClient(address);
                });
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder
                    .UseConsulClientClustering(gatewayOptions =>
                    {
                        var address = new Uri(ConsulTestUtils.CONSUL_ENDPOINT);
                        gatewayOptions.ConfigureConsulClient(address);
                    });
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
