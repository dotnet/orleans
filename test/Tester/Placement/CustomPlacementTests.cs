using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;

namespace Tester.CustomPlacementTests
{
    [TestCategory("Functional"), TestCategory("Placement")]
    public class CustomPlacementTests : OrleansTestingBase, IClassFixture<CustomPlacementTests.Fixture>
    {
        private const short nSilos = 3;
        private readonly Fixture fixture;
        private string[] silos;

        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(nSilos);
                options.ClusterConfiguration.UseStartupType<TestStartup>();
                return new TestCluster(options);
            }
        }

        public CustomPlacementTests(Fixture fixture)
        {
            this.fixture = fixture;

            // sort silo IDs into an array
            this.silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress.ToString()).OrderBy(s => s).ToArray();
        }

        [Fact]
        public async Task CustomPlacement_FixedSilo()
        {
            const int nGrains = 100;

            Task<string>[] tasks = new Task<string>[nGrains];
            for (int i = 0; i < nGrains; i++)
            {
                var g = this.fixture.GrainFactory.GetGrain<ICustomPlacementTestGrain>(Guid.NewGuid(),
                    "UnitTests.Grains.CustomPlacement_FixedSiloGrain");
                tasks[i] = g.GetRuntimeInstanceId();
            }

            await Task.WhenAll(tasks);

            var silo = tasks[0].Result;
            Assert.Equal(silos[silos.Length-2], silo);

            for (int i = 1; i < nGrains; i++)
            {
                Assert.Equal(silo, tasks[i].Result);
            }
        }

        [Fact]
        public async Task CustomPlacement_ExcludeOne()
        {
            const int nGrains = 100;

            Task<string>[] tasks = new Task<string>[nGrains];
            for (int i = 0; i < nGrains; i++)
            {
                var g = this.fixture.GrainFactory.GetGrain<ICustomPlacementTestGrain>(Guid.NewGuid(),
                    "UnitTests.Grains.CustomPlacement_ExcludeOneGrain");
                tasks[i] = g.GetRuntimeInstanceId();
            }

            await Task.WhenAll(tasks);
            var excludedSilo = silos[1];

            for (int i = 1; i < nGrains; i++)
            {
                Assert.NotEqual(excludedSilo, tasks[i].Result);
            }
        }
    }

    public class TestStartup
    {
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IPlacementDirector<TestCustomPlacementStrategy>, TestPlacementStrategyFixedSiloDirector>();

            return services.BuildServiceProvider();
        }
    }
}
