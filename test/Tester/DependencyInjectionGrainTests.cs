using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
        private class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartPrimary = true,
                    StartSecondary = false,
                    SiloConfigFile = new FileInfo("OrleansStartupConfigurationForTesting.xml")
                });
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
