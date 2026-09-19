using System.Collections.Immutable;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// A view derived from a canonical membership snapshot and fixed, agreed service configuration.
/// </summary>
internal sealed class MembershipBasedClusterServiceView : IClusterServiceView<ClusterServiceViewId>
{
    public MembershipBasedClusterServiceView(
        ClusterMembershipSnapshot snapshot,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries,
        long providerEpoch = 0)
    {
        Id = GetId(snapshot, providerEpoch);
        PreviousViewId = snapshot.Version == MembershipVersion.MinValue ? null : new(providerEpoch, new(snapshot.Version.Value - 1));
        Topology = CreateTopology(snapshot, configuration, getRingBoundaries);
        ClusterMembershipSnapshot = snapshot;
        Configuration = configuration;
    }

    public ClusterServiceViewId Id { get; }

    public ClusterServiceViewId? PreviousViewId { get; }

    public ClusterServiceTopology Topology { get; }

    public bool TryGetPredecessor(out ClusterServiceViewId predecessor)
    {
        predecessor = PreviousViewId.GetValueOrDefault();
        return PreviousViewId.HasValue;
    }

    public bool IsDirectSuccessorOf(MembershipBasedClusterServiceView previous) =>
        PreviousViewId == previous.Id
        && Configuration.ServiceId == previous.Configuration.ServiceId
        && Configuration.PartitionsPerSilo == previous.Configuration.PartitionsPerSilo
        && Configuration.AssignmentStrategy == previous.Configuration.AssignmentStrategy;

    public ClusterMembershipSnapshot ClusterMembershipSnapshot { get; }

    public ClusterServiceConfiguration Configuration { get; }

    private static ClusterServiceViewId GetId(ClusterMembershipSnapshot snapshot, long providerEpoch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(providerEpoch, 0);
        return new(providerEpoch, new(snapshot.Version.Value));
    }

    private static ClusterServiceTopology CreateTopology(
        ClusterMembershipSnapshot snapshot,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var members = ImmutableArray.CreateBuilder<SiloAddress>();
        foreach (var (address, member) in snapshot.Members)
        {
            if (member.Status == SiloStatus.Active)
            {
                members.Add(address);
            }
        }

        return new(members.ToImmutable(), configuration.PartitionsPerSilo, getRingBoundaries);
    }
}
