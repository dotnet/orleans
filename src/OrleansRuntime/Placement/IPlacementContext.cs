using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.Placement
{
    internal interface IPlacementContext
    {
        Logger Logger { get; }

        /// <summary>
        /// Lookup locally known directory information for a target grain
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="addresses">Local addresses will always be complete, remote may be partial</param>
        /// <returns>True if remote addresses are complete within freshness constraint</returns>
        bool FastLookup(GrainId grain, out AddressesAndTag addresses);

        Task<AddressesAndTag> FullLookup(GrainId grain);

        Task<AddressesAndTag> LookupInCluster(GrainId grain, string clusterId);

        bool LocalLookup(GrainId grain, out List<ActivationData> addresses);

        IList<SiloAddress> GetCompatibleSiloList(GrainId grain);

        SiloAddress LocalSilo { get; }

        SiloStatus LocalSiloStatus { get; }

        /// <summary>
        /// Try to get the transaction state of the activation if it is available on this silo
        /// </summary>
        /// <param name="id"></param>
        /// <param name="activationData"></param>
        /// <returns></returns>
        bool TryGetActivationData(ActivationId id, out ActivationData activationData);

        void GetGrainTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, out MultiClusterRegistrationStrategy strategy, string genericArguments = null);
    }

    internal static class PlacementContextExtensions
    {
        public static Task<AddressesAndTag> Lookup(this IPlacementContext @this, GrainId grainId)
        {
            AddressesAndTag l;
            return @this.FastLookup(grainId, out l) ? Task.FromResult(l) : @this.FullLookup(grainId); 
        }

        public static PlacementStrategy GetGrainPlacementStrategy(this IPlacementContext @this, int typeCode, string genericArguments = null)
        {
            string unused;
            PlacementStrategy placement;
            MultiClusterRegistrationStrategy unusedActivationStrategy;
            @this.GetGrainTypeInfo(typeCode, out unused, out placement, out unusedActivationStrategy, genericArguments);
            return placement;
        }

        public static PlacementStrategy GetGrainPlacementStrategy(this IPlacementContext @this, GrainId grainId, string genericArguments = null)
        {
            return @this.GetGrainPlacementStrategy(grainId.GetTypeCode(), genericArguments);
        }

        public static string GetGrainTypeName(this IPlacementContext @this, int typeCode, string genericArguments = null)
        {
            string grainClass;
            PlacementStrategy unused;
            MultiClusterRegistrationStrategy unusedActivationStrategy;
            @this.GetGrainTypeInfo(typeCode, out grainClass, out unused, out unusedActivationStrategy, genericArguments);
            return grainClass;
        }

        public static string GetGrainTypeName(this IPlacementContext @this, GrainId grainId, string genericArguments = null)
        {
            return @this.GetGrainTypeName(grainId.GetTypeCode(), genericArguments);
        }

        public static void GetGrainTypeInfo(this IPlacementContext @this, GrainId grainId, out string grainClass, out PlacementStrategy placement, out MultiClusterRegistrationStrategy activationStrategy, string genericArguments = null)
        {
            @this.GetGrainTypeInfo(grainId.GetTypeCode(), out grainClass, out placement, out activationStrategy, genericArguments);
        }
    }
}
