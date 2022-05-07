using System.IO;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace UnitTests.TestHelper
{
    public static class TestUtils
    {
        /// <summary>Gets a detailed grain report from a specified silo</summary>
        /// <param name="grainFactory">The grain factory.</param>
        /// <param name="grainId">The grain id we are requesting information from</param>
        /// <param name="siloHandle">The target silo that should provide this information from it's cache</param>
        internal static Task<DetailedGrainReport> GetDetailedGrainReport(IInternalGrainFactory grainFactory, GrainId grainId, SiloHandle siloHandle)
        {
            // Use the siloAddress here, not the gateway address, since we may be targeting a silo on which we are not 
            // connected to the gateway
            var siloControl = grainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlType, siloHandle.SiloAddress);
            return siloControl.GetDetailedGrainReport(grainId);
        }
    }
}
