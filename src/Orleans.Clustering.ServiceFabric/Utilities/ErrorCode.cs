using System.Diagnostics.CodeAnalysis;

namespace Orleans.Clustering.ServiceFabric.Utilities
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal enum ErrorCode
    {
        Runtime = 100000,
        ServiceFabricBase = Runtime + 4400,
        ServiceFabric_GatewayProvider_ExceptionNotifyingSubscribers = ServiceFabricBase + 1,
        ServiceFabric_GatewayProvider_ExceptionRefreshingGateways = ServiceFabricBase + 2,
        ServiceFabric_MembershipOracle_ExceptionNotifyingSubscribers = ServiceFabricBase + 3,
        ServiceFabric_MembershipOracle_ExceptionRefreshingPartitions = ServiceFabricBase + 4,
        ServiceFabric_Resolver_PartitionNotFound = ServiceFabricBase + 5,
        ServiceFabric_Resolver_PartitionResolutionException = ServiceFabricBase + 6,
        ServiceFabric_MembershipOracle_EncounteredUndeadSilo = ServiceFabricBase + 7,
        ServiceFabric_MembershipOracle_PartitionResolutionException = ServiceFabricBase + 8,
    }
}
