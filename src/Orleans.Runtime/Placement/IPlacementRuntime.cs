using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.Placement
{
    internal interface IPlacementRuntime : IPlacementContext
    {
        /// <summary>
        /// Lookup locally known directory information for a target grain
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="addresses">Local addresses will always be complete, remote may be partial</param>
        /// <returns>True if remote addresses are complete within freshness constraint</returns>
        bool FastLookup(GrainId grain, out List<ActivationAddress> addresses);

        Task<List<ActivationAddress>> FullLookup(GrainId grain);

        bool LocalLookup(GrainId grain, out List<ActivationData> addresses);
        
        /// <summary>
        /// Try to get the transaction state of the activation if it is available on this silo
        /// </summary>
        /// <param name="id"></param>
        /// <param name="activationData"></param>
        /// <returns></returns>
        bool TryGetActivationData(ActivationId id, out ActivationData activationData);
    }

    internal static class PlacementRuntimeExtensions
    {
        public static string GetGrainTypeName(this IPlacementRuntime @this, GrainId grainId, string genericArguments = null)
        {
            return @this.GetGrainTypeName(grainId, genericArguments);
        }

        public static void GetGrainTypeInfo(this IPlacementRuntime @this, GrainId grainId, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            @this.GetGrainTypeInfo(grainId, out grainClass, out placement, genericArguments);
        }
    }
}
