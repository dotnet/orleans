using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces.Directories;
using UnitTests.Grains.Directories;
using Xunit;

namespace Tester.Directories
{
    public class MultipleGrainDirectoriesTests : TestClusterPerTest
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.AddAzureTableGrainDirectory(
                    AzureTableDirectoryGrain.DIRECTORY,
                    options => options.ConnectionString = TestDefaultConfiguration.DataConnectionString);
            }
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        [SkippableFact, TestCategory("Directory"), TestCategory("Functionals")]
        public async Task PingGrain()
        {
            var grainOnPrimary = await GetGrainOnPrimary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the primary silo");
            var grainOnSecondary = await GetGrainOnSecondary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the secondary silo");

            // Setup
            var primaryCounter = await grainOnPrimary.Ping();
            var secondaryCounter = await grainOnSecondary.Ping();

            // Each silo see the activation on the other silo
            Assert.Equal(++primaryCounter, await grainOnSecondary.ProxyPing(grainOnPrimary));
            Assert.Equal(++secondaryCounter, await grainOnPrimary.ProxyPing(grainOnSecondary));

            await Task.Delay(5000);

            // Shutdown the secondary silo
            await this.HostedCluster.StopSecondarySilosAsync();

            // Activation on the primary silo should still be there, another activation should be
            // created for the other one
            Assert.Equal(++primaryCounter, await grainOnPrimary.Ping());
            Assert.Equal(1, await grainOnSecondary.Ping());
        }

        private async Task<IAzureTableDirectoryGrain> GetGrainOnPrimary()
        {
            while (true)
            {
                var grain = this.GrainFactory.GetGrain<IAzureTableDirectoryGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.Primary.SiloAddress.Endpoint.ToString()))
                    return grain;
            }
        }

        private async Task<IAzureTableDirectoryGrain> GetGrainOnSecondary()
        {
            while (true)
            {
                var grain = this.GrainFactory.GetGrain<IAzureTableDirectoryGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                    return grain;
            }
        }
    }
}
