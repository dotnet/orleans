namespace Orleans.Runtime.ClusterServices;

internal enum ClusterServiceFencingMode
{
    MembershipView,
    TimedSafetyLease,
    External
}

[GenerateSerializer, Immutable, Alias(nameof(ClusterServiceFence))]
internal readonly record struct ClusterServiceFence(
    [property: Id(0)] ClusterServiceFencingMode Mode,
    [property: Id(1)] long Token);
