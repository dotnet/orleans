using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// An assignment revision, not a storage ETag, membership version, or permission to perform external effects.
/// Recreating the register requires a new authority namespace and an explicit service bootstrap.
/// </summary>
internal readonly record struct RegisteredServiceViewId : IComparable<RegisteredServiceViewId>
{
    public RegisteredServiceViewId(string serviceId, string authorityId, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityId);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ServiceId = serviceId;
        AuthorityId = authorityId;
        Revision = revision;
    }

    public string ServiceId { get; }
    public string AuthorityId { get; }
    public long Revision { get; }

    public int CompareTo(RegisteredServiceViewId other)
    {
        if (!StringComparer.Ordinal.Equals(ServiceId, other.ServiceId)
            || !StringComparer.Ordinal.Equals(AuthorityId, other.AuthorityId))
        {
            throw new ClusterServiceAuthorityException($"Cannot compare '{this}' with unrelated service authority '{other}'.");
        }

        return Revision.CompareTo(other.Revision);
    }

    public override string ToString() => $"{ServiceId}/{AuthorityId}/{Revision}";
}

internal sealed class ClusterServiceAuthorityException(string message) : InvalidOperationException(message);

internal sealed class ClusterServiceViewUnavailableException(string message) : InvalidOperationException(message);

internal sealed record RegisteredServiceConfiguration
{
    public RegisteredServiceConfiguration(int protocolVersion, string fingerprint, string payload, int partitionsPerSilo = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(protocolVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ArgumentNullException.ThrowIfNull(payload);
        ProtocolVersion = protocolVersion;
        Fingerprint = fingerprint;
        Payload = payload;
        PartitionsPerSilo = partitionsPerSilo;
    }

    public int ProtocolVersion { get; }
    public string Fingerprint { get; }
    public string Payload { get; }
    public int PartitionsPerSilo { get; }
}

/// <summary>
/// One complete immutable publication. Both resource indexes are constructed together in O(resources);
/// local transition planning subsequently compares only the two local owned sets.
/// String resource identities are ordinal and service-scoped; adapters can encode qualified queue identities.
/// </summary>
internal sealed class RegisteredClusterServiceView : IClusterServiceView<RegisteredServiceViewId>
{
    private static readonly FrozenSet<string> EmptyResources = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    internal RegisteredClusterServiceView(
        RegisteredServiceViewId id,
        RegisteredServiceViewId? predecessor,
        MembershipVersion membershipWatermark,
        RegisteredServiceConfiguration configuration,
        IEnumerable<SiloAddress> participants,
        IEnumerable<string> resources,
        IEnumerable<KeyValuePair<string, SiloAddress>> assignments,
        ImmutableArray<ClusterServicePartitionAssignment> ringAssignments = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id.Revision, 1);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(assignments);
        if (predecessor is { } previous && (id.CompareTo(previous) <= 0 || previous.Revision == 0))
        {
            throw new ArgumentException("The authoritative predecessor must be an earlier published view.", nameof(predecessor));
        }

        Id = id;
        Predecessor = predecessor;
        MembershipWatermark = membershipWatermark;
        Configuration = configuration;
        Participants = participants.OrderBy(static silo => silo).ToImmutableArray();
        var owners = new Dictionary<SiloAddress, HashSet<string>>();
        foreach (var participant in Participants)
        {
            if (participant is null || !owners.TryAdd(participant, new(StringComparer.Ordinal)))
            {
                throw new ArgumentException("Participants must be unique silo incarnations.", nameof(participants));
            }
        }

        var catalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resource);
            if (!catalog.Add(resource))
            {
                throw new ArgumentException("Resource identities must be unique.", nameof(resources));
            }
        }

        var forward = new Dictionary<string, SiloAddress>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (assignment.Key is null || !catalog.Contains(assignment.Key)
                || assignment.Value is null || !owners.TryGetValue(assignment.Value, out var owned)
                || !forward.TryAdd(assignment.Key, assignment.Value))
            {
                throw new ArgumentException("Every assignment requires a unique declared resource and eligible owner.", nameof(assignments));
            }

            owned.Add(assignment.Key);
        }

        if (forward.Count != catalog.Count)
        {
            throw new ArgumentException("Assignments must cover the declared resource set exactly once.", nameof(assignments));
        }

        ResourceOwners = forward.ToFrozenDictionary(StringComparer.Ordinal);
        OwnerResources = owners.ToFrozenDictionary(static entry => entry.Key, static entry => entry.Value.ToFrozenSet(StringComparer.Ordinal));
        Resources = catalog.Order(StringComparer.Ordinal).ToImmutableArray();
        if (!ringAssignments.IsDefault)
        {
            Topology = new(Participants, configuration.PartitionsPerSilo, ringAssignments);
            RingAssignments = ringAssignments.OrderBy(static assignment => assignment.Range.Start).ToImmutableArray();
        }
    }

    public RegisteredServiceViewId Id { get; }
    public RegisteredServiceViewId? Predecessor { get; }
    public MembershipVersion MembershipWatermark { get; }
    public RegisteredServiceConfiguration Configuration { get; }
    public ImmutableArray<SiloAddress> Participants { get; }
    public ImmutableArray<string> Resources { get; }
    public FrozenDictionary<string, SiloAddress> ResourceOwners { get; }
    public FrozenDictionary<SiloAddress, FrozenSet<string>> OwnerResources { get; }
    public ImmutableArray<ClusterServicePartitionAssignment> RingAssignments { get; }
    public ClusterServiceTopology? Topology { get; }

    public FrozenSet<string> GetOwnedResources(SiloAddress silo) =>
        OwnerResources.TryGetValue(silo, out var resources) ? resources : EmptyResources;

    public bool TryGetPredecessor(out RegisteredServiceViewId predecessor)
    {
        predecessor = Predecessor.GetValueOrDefault();
        return Predecessor.HasValue;
    }

    public bool HasSameContent(RegisteredClusterServiceView other) =>
        Id == other.Id
        && Predecessor == other.Predecessor
        && MembershipWatermark == other.MembershipWatermark
        && Configuration == other.Configuration
        && Participants.SequenceEqual(other.Participants)
        && Resources.SequenceEqual(other.Resources)
        && ResourceOwners.All(entry => other.ResourceOwners.TryGetValue(entry.Key, out var owner) && owner.Equals(entry.Value))
        && RingAssignments.IsDefault == other.RingAssignments.IsDefault
        && (RingAssignments.IsDefault || RingAssignments.SequenceEqual(other.RingAssignments));

    public override string ToString() =>
        $"{Id} predecessor={Predecessor?.ToString() ?? "bootstrap"} membership={MembershipWatermark} configuration={Configuration.Fingerprint} participants={Participants.Length} resources={Resources.Length}";
}
