using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal interface IPlacementRuntime : IPlacementContext
    {
        /// <summary>
        /// Lookup locally known directory information for a target grain
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="address">Local address will always be complete, remote may be partial</param>
        /// <returns>True if remote addresses are complete within freshness constraint</returns>
        bool FastLookup(GrainId grain, out ActivationAddress address);

        Task<ActivationAddress> FullLookup(GrainId grain);

        bool TryGetActivation(GrainId grain, out ActivationData activation);
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
