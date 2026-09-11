using System.Collections.Immutable;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// A canonical service view. Derived views carry the configuration and metadata supplied by their authority.
/// </summary>
internal abstract class ClusterServiceView
{
    protected ClusterServiceView(
        ClusterServiceViewId id,
        ClusterServiceViewId? previousViewId,
        ClusterServiceTopology topology)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id.ProviderEpoch, 0);
        ArgumentNullException.ThrowIfNull(topology);
        if (previousViewId is { } previous && previous >= id)
        {
            throw new ArgumentException("The predecessor must be older than this view.", nameof(previousViewId));
        }

        Id = id;
        PreviousViewId = previousViewId;
        Topology = topology;
    }

    public ClusterServiceViewId Id { get; }

    /// <summary>
    /// The authoritative predecessor, when known, rather than the reader's previously observed view.
    /// </summary>
    public ClusterServiceViewId? PreviousViewId { get; }

    public ClusterServiceTopology Topology { get; }

    public bool IsDirectSuccessorOf(ClusterServiceView previous) => PreviousViewId == previous.Id;
}

/// <summary>
/// A view derived from a canonical membership snapshot and fixed, agreed service configuration.
/// </summary>
internal sealed class MembershipBasedClusterServiceView : ClusterServiceView
{
    public MembershipBasedClusterServiceView(
        ClusterMembershipSnapshot snapshot,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries,
        long providerEpoch = 0)
        : base(
            GetId(snapshot, providerEpoch),
            snapshot.Version == MembershipVersion.MinValue ? null : new(providerEpoch, new(snapshot.Version.Value - 1)),
            CreateTopology(snapshot, configuration, getRingBoundaries))
    {
        ClusterMembershipSnapshot = snapshot;
        Configuration = configuration;
    }

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
