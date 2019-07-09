using System.Threading.Tasks;
using Orleans;
using Orleans.Placement;
using Orleans.Runtime;
using UnitTests.GrainInterfaces.Placement;

namespace UnitTests.Grains.Placement
{
    [SiloServicePlacement]
    public class SiloServicePlacementGrain : Grain, ISiloServicePlacementGrain
    {
        private readonly IGrainRuntime runtime;

        public SiloServicePlacementGrain(IGrainRuntime runtime)
        {
            this.runtime = runtime;
        }

        public Task<SiloAddress> GetSilo()
        {
            return  Task.FromResult(this.runtime.SiloAddress);
        }

        public Task<string> GetKey()
        {
            return Task.FromResult(SiloServicePlacementKeyFormat.TryParsePrimaryKey(this.GetPrimaryKeyString(), out string key) ? key : default(string));
        }
    }
}
