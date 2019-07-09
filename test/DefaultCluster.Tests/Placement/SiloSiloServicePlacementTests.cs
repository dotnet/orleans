using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Placement;
using TestExtensions;
using UnitTests.GrainInterfaces.Placement;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.Placement
{
    public class SiloServicePlacementTests : BaseTestClusterFixture
    {
        private readonly ITestOutputHelper output;

        public SiloServicePlacementTests(ITestOutputHelper output)
        {
            this.output = output;
            output.WriteLine("SiloServicePlacementTests - constructor");
        }

        [Fact, TestCategory("Placement"), TestCategory("Functional")]
        public async Task SiloPlacementShouldPlaceOnSilo()
        {
            const string ExpectedKey = "Test";
            List<SiloAddress> silos = await GetSilos();

            foreach(SiloAddress silo in silos)
            {
                output.WriteLine($"Placing service \"{ExpectedKey}\" with surface {nameof(ISiloServicePlacementGrain)} on silo {silo}");
                ISiloServicePlacementGrain grain = this.GrainFactory.GetGrain<ISiloServicePlacementGrain>(silo, ExpectedKey);
                SiloAddress actualSilo = await grain.GetSilo();
                Assert.Equal(silo, actualSilo);
                string actualKey = await grain.GetKey();
                Assert.Equal(ExpectedKey, actualKey);
            }
        }

        private async Task<List<SiloAddress>> GetSilos()
        {
            IManagementGrain mgmtGrain = this.GrainFactory.GetGrain<IManagementGrain>(0);
            return (await mgmtGrain.GetHosts(true)).Keys.ToList();
        }
    }
}
