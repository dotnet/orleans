using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.ClusterServices;

internal sealed class ClusterServiceTopology
{
    private readonly ImmutableArray<(uint Start, int MemberIndex, int PartitionIndex)> _ringBoundaries;
    private readonly RingRangeCollection[] _rangesByMember;
    private readonly ImmutableArray<ImmutableArray<RingRange>> _rangesByMemberPartition;

    public ClusterServiceTopology(
        ClusterMembershipSnapshot snapshot,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(getRingBoundaries);

        Configuration = configuration;
        ClusterMembershipSnapshot = snapshot;
        ViewId = new(snapshot.Version, configuration.ProtocolVersion, configuration.Fingerprint);

        var sortedActiveMembers = ImmutableArray.CreateBuilder<SiloAddress>(
            snapshot.Members.Count(static member => member.Value.Status == SiloStatus.Active));
        foreach (var member in snapshot.Members)
        {
            if (member.Value.Status == SiloStatus.Active)
            {
                sortedActiveMembers.Add(member.Key);
            }
        }

        sortedActiveMembers.Sort(static (left, right) => left.CompareTo(right));
        var boundaries = ImmutableArray.CreateBuilder<(uint Hash, int MemberIndex, int PartitionIndex)>(
            configuration.PartitionsPerSilo * sortedActiveMembers.Count);
        for (var memberIndex = 0; memberIndex < sortedActiveMembers.Count; memberIndex++)
        {
            var hashCodes = getRingBoundaries(sortedActiveMembers[memberIndex], configuration.PartitionsPerSilo);
            if (hashCodes.Length != configuration.PartitionsPerSilo)
            {
                throw new InvalidOperationException(
                    $"Assignment strategy '{configuration.AssignmentStrategy}' returned {hashCodes.Length} boundaries for "
                    + $"{configuration.PartitionsPerSilo} partitions.");
            }

            for (var partitionIndex = 0; partitionIndex < hashCodes.Length; partitionIndex++)
            {
                boundaries.Add((hashCodes[partitionIndex], memberIndex, partitionIndex));
            }
        }

        boundaries.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            var partitionCompare = left.PartitionIndex.CompareTo(right.PartitionIndex);
            return partitionCompare != 0
                ? partitionCompare
                : left.MemberIndex.CompareTo(right.MemberIndex);
        });

        for (var index = 1; index < boundaries.Count;)
        {
            if (boundaries[index].Hash == boundaries[index - 1].Hash)
            {
                boundaries.RemoveAt(index - 1);
            }
            else
            {
                index++;
            }
        }

        _ringBoundaries = boundaries.ToImmutable();
        Members = sortedActiveMembers.ToImmutable();

        var rangesByMemberPartition = new RingRange[Members.Length][];
        for (var memberIndex = 0; memberIndex < Members.Length; memberIndex++)
        {
            rangesByMemberPartition[memberIndex] = new RingRange[configuration.PartitionsPerSilo];
        }

        for (var index = 0; index < _ringBoundaries.Length; index++)
        {
            var (entryStart, memberIndex, partitionIndex) = _ringBoundaries[index];
            var (nextStart, _, _) = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
            rangesByMemberPartition[memberIndex][partitionIndex] = entryStart == nextStart
                ? _ringBoundaries.Length == 1 ? RingRange.Full : RingRange.Empty
                : RingRange.Create(entryStart, nextStart);
        }

        var ranges = ImmutableArray.CreateBuilder<ImmutableArray<RingRange>>(Members.Length);
        for (var memberIndex = 0; memberIndex < Members.Length; memberIndex++)
        {
            ranges.Add(ImmutableArray.CreateRange(rangesByMemberPartition[memberIndex]));
        }

        _rangesByMemberPartition = ranges.ToImmutable();
        _rangesByMember = new RingRangeCollection[Members.Length];
    }

    public ClusterServiceConfiguration Configuration { get; }

    public ClusterServiceViewId ViewId { get; }

    public ClusterMembershipSnapshot ClusterMembershipSnapshot { get; }

    public ImmutableArray<SiloAddress> Members { get; }

    public int PartitionCount => Configuration.PartitionsPerSilo;

    public RangeCollection RangeOwners => new(this);

    public RingRange GetRange(SiloAddress address, int partitionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionIndex, 0);
        if (partitionIndex >= PartitionCount)
        {
            return RingRange.Empty;
        }

        var memberIndex = TryGetMemberIndex(address);
        return memberIndex < 0 ? RingRange.Empty : _rangesByMemberPartition[memberIndex][partitionIndex];
    }

    public RingRangeCollection GetMemberRanges(SiloAddress address)
    {
        var memberIndex = TryGetMemberIndex(address);
        if (memberIndex < 0)
        {
            return RingRangeCollection.Empty;
        }

        var result = _rangesByMember[memberIndex];
        if (result.IsDefault)
        {
            result = _rangesByMember[memberIndex] = RingRangeCollection.Create(_rangesByMemberPartition[memberIndex]);
        }

        return result;
    }

    public ImmutableArray<RingRange> GetMemberRangesByPartition(SiloAddress address)
    {
        var memberIndex = TryGetMemberIndex(address);
        return memberIndex < 0 ? [] : _rangesByMemberPartition[memberIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(uint hashCode, out ClusterServicePartitionOwner owner)
    {
        var index = SearchAlgorithms.RingRangeBinarySearch(
            _ringBoundaries.Length,
            this,
            static (topology, rangeIndex) => topology.GetRangeCore(rangeIndex),
            hashCode);
        if (index >= 0)
        {
            var (_, memberIndex, partitionIndex) = _ringBoundaries[index];
            owner = new(
                Members[memberIndex],
                memberIndex,
                partitionIndex,
                GetRangeCore(index));
            return true;
        }

        Debug.Assert(Members.Length == 0);
        owner = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TryGetMemberIndex(SiloAddress? address)
    {
        if (address is null)
        {
            return -1;
        }

        return SearchAlgorithms.BinarySearch(
            Members.Length,
            (this, address),
            static (index, state) => state.Item1.Members[index].CompareTo(state.address));
    }

    private RingRange GetRangeCore(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var (entryStart, _, _) = _ringBoundaries[index];
        var (nextStart, _, _) = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
        if (entryStart == nextStart)
        {
            return _ringBoundaries.Length == 1 ? RingRange.Full : RingRange.Empty;
        }

        return RingRange.Create(entryStart, nextStart);
    }

    private ClusterServicePartitionOwner GetOwner(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);
        var (_, memberIndex, partitionIndex) = _ringBoundaries[index];
        return new(Members[memberIndex], memberIndex, partitionIndex, GetRangeCore(index));
    }

    public readonly struct RangeCollection(ClusterServiceTopology topology) : IReadOnlyList<ClusterServicePartitionOwner>
    {
        public int Count => topology._ringBoundaries.Length;

        public ClusterServicePartitionOwner this[int index] => topology.GetOwner(index);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        IEnumerator<ClusterServicePartitionOwner> IEnumerable<ClusterServicePartitionOwner>.GetEnumerator() => GetEnumerator();

        public RangeCollectionEnumerator GetEnumerator() => new(topology);

        public struct RangeCollectionEnumerator(ClusterServiceTopology topology) : IEnumerator<ClusterServicePartitionOwner>
        {
            private int _index;

            public readonly ClusterServicePartitionOwner Current => topology.GetOwner(_index - 1);

            readonly object IEnumerator.Current => Current;

            public void Dispose() => _index = int.MaxValue;

            public bool MoveNext() => _index >= 0 && _index++ < topology._ringBoundaries.Length;

            public void Reset() => _index = 0;
        }
    }
}

internal readonly record struct ClusterServicePartitionOwner(
    SiloAddress SiloAddress,
    int MemberIndex,
    int PartitionIndex,
    RingRange Range);
