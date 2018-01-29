using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Hosting;

namespace Tester.CustomPlacementTests
{
    [TestCategory("Functional"), TestCategory("Placement")]
    public class CustomPlacementTests : OrleansTestingBase, IClassFixture<CustomPlacementTests.Fixture>
    {
        private const short nSilos = 3;
        private readonly Fixture fixture;
        private readonly string[] silos;
        private readonly SiloAddress[] siloAddresses;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = nSilos;
                builder.AddSiloBuilderConfigurator<TestSiloBuilderConfigurator>();
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.AssumeHomogenousSilosForTesting = false;
                    legacy.ClusterConfiguration.Globals.TypeMapRefreshInterval = TimeSpan.FromMilliseconds(100);
                });
            }

            private class TestSiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.ConfigureServices(services =>
                        services.AddSingleton<IPlacementDirector<TestCustomPlacementStrategy>, TestPlacementStrategyFixedSiloDirector>());
                }
            }
        }

        public CustomPlacementTests(Fixture fixture)
        {
            this.fixture = fixture;

            // sort silo IDs into an array
            this.silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress.ToString()).OrderBy(s => s).ToArray();
            this.siloAddresses = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s.ToString()).ToArray();
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

        [Fact]
        public async Task CustomPlacement_RequestContextBased()
        {
            const int nGrains = 100;
            var targetSilo = silos.Length - 1; // Always target the last one

            Task<string>[] tasks = new Task<string>[nGrains];
            for (int i = 0; i < nGrains; i++)
            {
                RequestContext.Set(TestPlacementStrategyFixedSiloDirector.TARGET_SILO_INDEX, targetSilo);
                var g = this.fixture.GrainFactory.GetGrain<ICustomPlacementTestGrain>(Guid.NewGuid(),
                    "UnitTests.Grains.CustomPlacement_RequestContextBased");
                tasks[i] = g.GetRuntimeInstanceId();
                RequestContext.Clear();
            }

            await Task.WhenAll(tasks);

            for (int i = 1; i < nGrains; i++)
            {
                Assert.Equal(silos[targetSilo], tasks[i].Result);
            }
        }

        [Fact]
        public async Task HashBasedPlacement()
        {
            const int nGrains = 100;

            Task<SiloAddress>[] tasks = new Task<SiloAddress>[nGrains];
            List<IGrainIdentity> grains = new List<IGrainIdentity>();
            for (int i = 0; i < nGrains; i++)
            {
                var g = this.fixture.GrainFactory.GetGrain<IHashBasedPlacementGrain>(Guid.NewGuid(),
                    "UnitTests.Grains.HashBasedBasedPlacementGrain");
                grains.Add(g.GetGrainIdentity());
                tasks[i] = g.GetSiloAddress();
            }

            await Task.WhenAll(tasks);

            for (int i = 0; i < nGrains; i++)
            {
                var hash = (int) (grains[i].GetUniformHashCode() & 0x7fffffff);
                Assert.Equal(siloAddresses[hash % silos.Length], tasks[i].Result);
            }
        }
    }
}
