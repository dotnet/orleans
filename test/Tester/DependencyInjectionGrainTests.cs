using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;
using Tester;
using Xunit;

namespace UnitTests.General
{
    public class DependencyInjectionGrainTests : OrleansTestingBase, IClassFixture<DependencyInjectionGrainTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.ApplyToAllNodes(nodeConfig => nodeConfig.StartupTypeName = typeof(TestStartup).AssemblyQualifiedName);
                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task DiTests_SimpleDiGrainGetGrain()
        {
            ISimpleDIGrain grain = GrainFactory.GetGrain<ISimpleDIGrain>(GetRandomGrainId());
            long ignored = await grain.GetTicksFromService();
        }
    }

    public class TestStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IInjectedService, InjectedService>();

            services.AddTransient<SimpleDIGrain>();

            return services.BuildServiceProvider();
        }
    }
}
