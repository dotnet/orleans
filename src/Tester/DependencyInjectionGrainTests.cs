using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

namespace UnitTests.General
{
    [DeploymentItem("OrleansStartupConfigurationForTesting.xml")]
    [DeploymentItem("OrleansDependencyInjection.dll")]
    [TestClass]
    public class DependencyInjectionGrainTests : UnitTestSiloHost
    {
        public DependencyInjectionGrainTests()
            : base(new TestingSiloOptions
            {
                StartPrimary = true,
                StartSecondary = false,
                SiloConfigFile = new FileInfo("OrleansStartupConfigurationForTesting.xml")
            })
        {
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Functional")]
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
