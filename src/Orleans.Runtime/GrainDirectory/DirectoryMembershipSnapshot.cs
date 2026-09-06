using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Runtime.ClusterServices;

namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryMembershipSnapshot
{
    internal const string ServiceId = "orleans-grain-directory";
    internal const long ProviderEpoch = 0;
    private const string AssignmentStrategy = "uniform-hash-ring/v1";

    internal static readonly Func<SiloAddress, int, uint[]> DefaultGetRingBoundaries = static (silo, count) =>
    {
        if (count == 1)
        {
            return [unchecked((uint)silo.GetConsistentHashCode())];
        }

        return silo.GetUniformHashCodes(count);
    };

    private readonly ClusterServiceTopology _topology;
    private readonly MembershipBasedClusterServiceView _view;
    private readonly ImmutableArray<ImmutableArray<IGrainDirectoryPartition>> _partitionsByMember;

    internal DirectoryMembershipSnapshot(
        ClusterMembershipSnapshot snapshot,
        IInternalGrainFactory grainFactory,
        int partitionCount,
        Func<SiloAddress, int, uint[]> getRingBoundaries)
        : this(
            new MembershipBasedClusterServiceView(
                snapshot,
                CreateConfiguration(partitionCount),
                getRingBoundaries),
            grainFactory)
    {
    }

    internal DirectoryMembershipSnapshot(
        MembershipBasedClusterServiceView view,
        IInternalGrainFactory grainFactory)
    {
        if (view.Id.ProviderEpoch != ProviderEpoch)
        {
            throw new ArgumentException(
                "The distributed directory's membership-version wire contract requires provider epoch zero.",
                nameof(view));
        }

        _view = view;
        var topology = view.Topology;
        _topology = topology;

        var memberPartitions = ImmutableArray.CreateBuilder<ImmutableArray<IGrainDirectoryPartition>>(topology.Members.Length);
        foreach (var activeMember in topology.Members)
        {
            var partitionReferences = ImmutableArray.CreateBuilder<IGrainDirectoryPartition>(topology.PartitionCount);
            for (var partitionIndex = 0; partitionIndex < topology.PartitionCount; partitionIndex++)
            {
                partitionReferences.Add(
                    grainFactory?.GetSystemTarget<IGrainDirectoryPartition>(
                        GrainDirectoryPartition.CreateGrainId(activeMember, partitionIndex).GrainId)!);
            }

            memberPartitions.Add(partitionReferences.ToImmutable());
        }

        _partitionsByMember = memberPartitions.ToImmutable();
    }

    public static DirectoryMembershipSnapshot Default { get; } = new(
        ClusterMembershipSnapshot.Default,
        null!,
        partitionCount: 1,
        DefaultGetRingBoundaries);

    public MembershipVersion Version => ClusterMembershipSnapshot.Version;

    internal ClusterServiceViewId ViewId => _view.Id;

    internal bool IsDirectSuccessorOf(DirectoryMembershipSnapshot previous) => _view.IsDirectSuccessorOf(previous._view);

    internal static ClusterServiceViewId GetViewId(MembershipVersion version) => new(ProviderEpoch, new(version.Value));

    internal int PartitionCount => _topology.PartitionCount;

    public ImmutableArray<SiloAddress> Members => _topology.Members;

    public ClusterMembershipSnapshot ClusterMembershipSnapshot => _view.ClusterMembershipSnapshot;

    public RingRange GetRange(SiloAddress address, int partitionIndex) =>
        _topology.GetRange(address, partitionIndex);

    public RingRangeCollection GetMemberRanges(SiloAddress address) =>
        _topology.GetMemberRanges(address);

    public ImmutableArray<RingRange> GetMemberRangesByPartition(SiloAddress address) =>
        _topology.GetMemberRangesByPartition(address);

    public RangeCollection RangeOwners => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(
        GrainId grainId,
        [NotNullWhen(true)] out SiloAddress? owner,
        [NotNullWhen(true)] out IGrainDirectoryPartition? partitionReference) =>
        TryGetOwner(grainId.GetUniformHashCode(), out owner, out partitionReference);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(
        uint hashCode,
        [NotNullWhen(true)] out SiloAddress? owner,
        [NotNullWhen(true)] out IGrainDirectoryPartition? partitionReference)
    {
        if (_topology.TryGetOwner(hashCode, out var partitionOwner))
        {
            owner = partitionOwner.SiloAddress;
            partitionReference = _partitionsByMember[partitionOwner.MemberIndex][partitionOwner.PartitionIndex];
            return true;
        }

        owner = null;
        partitionReference = null;
        return false;
    }

    internal static ClusterServiceConfiguration CreateConfiguration(int partitionCount) =>
        new(ServiceId, partitionCount, AssignmentStrategy);

    public readonly struct RangeCollection(DirectoryMembershipSnapshot snapshot)
        : IReadOnlyList<(RingRange Range, int MemberIndex, int PartitionIndex)>
    {
        public int Count => snapshot._topology.RangeOwners.Count;

        public (RingRange Range, int MemberIndex, int PartitionIndex) this[int index]
        {
            get
            {
                var owner = snapshot._topology.RangeOwners[index];
                return (owner.Range, owner.MemberIndex, owner.PartitionIndex);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        IEnumerator<(RingRange Range, int MemberIndex, int PartitionIndex)>
            IEnumerable<(RingRange Range, int MemberIndex, int PartitionIndex)>.GetEnumerator() =>
            GetEnumerator();

        public RangeCollectionEnumerator GetEnumerator() => new(snapshot);

        public struct RangeCollectionEnumerator(DirectoryMembershipSnapshot snapshot)
            : IEnumerator<(RingRange Range, int MemberIndex, int PartitionIndex)>
        {
            private int _index;

            public readonly (RingRange Range, int MemberIndex, int PartitionIndex) Current =>
                snapshot.RangeOwners[_index - 1];

            readonly object IEnumerator.Current => Current;

            public void Dispose() => _index = int.MaxValue;

            public bool MoveNext() => _index >= 0 && _index++ < snapshot.RangeOwners.Count;

            public void Reset() => _index = 0;
        }
    }
}
