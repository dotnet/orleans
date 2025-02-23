using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Orleans.Configuration;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryMembershipSnapshot
{
    internal const int PartitionsPerSilo = ConsistentRingOptions.DEFAULT_NUM_VIRTUAL_RING_BUCKETS;
    private readonly ImmutableArray<(uint Start, int MemberIndex, int PartitionIndex)> _ringBoundaries;
    private readonly RingRangeCollection[] _rangesByMember;
    private readonly ImmutableArray<ImmutableArray<IGrainDirectoryPartition>> _partitionsByMember;
    private readonly ImmutableArray<ImmutableArray<RingRange>> _rangesByMemberPartition;

    public DirectoryMembershipSnapshot(ClusterMembershipSnapshot snapshot, IInternalGrainFactory grainFactory) : this(snapshot, grainFactory, static (silo, count) => silo.GetUniformHashCodes(count))
    {
    }

    internal DirectoryMembershipSnapshot(ClusterMembershipSnapshot snapshot, IInternalGrainFactory grainFactory, Func<SiloAddress, int, uint[]> getRingBoundaries)
    {
        var sortedActiveMembers = ImmutableArray.CreateBuilder<SiloAddress>(snapshot.Members.Count(static m => m.Value.Status == SiloStatus.Active));
        foreach (var member in snapshot.Members)
        {
            // Only active members are part of directory membership.
            if (member.Value.Status == SiloStatus.Active)
            {
                sortedActiveMembers.Add(member.Key);
            }
        }

        sortedActiveMembers.Sort(static (left, right) => left.CompareTo(right));
        var hashIndexPairs = ImmutableArray.CreateBuilder<(uint Hash, int MemberIndex, int PartitionIndex)>(PartitionsPerSilo * sortedActiveMembers.Count);
        var memberPartitions = ImmutableArray.CreateBuilder<ImmutableArray<IGrainDirectoryPartition>>();
        for (var memberIndex = 0; memberIndex < sortedActiveMembers.Count; memberIndex++)
        {
            var activeMember = sortedActiveMembers[memberIndex];
            var hashCodes = getRingBoundaries(activeMember, PartitionsPerSilo).ToList();
            hashCodes.Sort();
            Debug.Assert(hashCodes.Count == PartitionsPerSilo);
            var partitionReferences = ImmutableArray.CreateBuilder<IGrainDirectoryPartition>(PartitionsPerSilo);
            for (var partitionIndex = 0; partitionIndex < hashCodes.Count; partitionIndex++)
            {
                var hashCode = hashCodes[partitionIndex];
                hashIndexPairs.Add((hashCode, memberIndex, partitionIndex));
                partitionReferences.Add(grainFactory?.GetSystemTarget<IGrainDirectoryPartition>(GrainDirectoryPartition.CreateGrainId(activeMember, partitionIndex).GrainId)!);
            }

            memberPartitions.Add(partitionReferences.ToImmutable());
        }

        _partitionsByMember = memberPartitions.ToImmutable();

        hashIndexPairs.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            var partitionCompare = left.PartitionIndex.CompareTo(right.PartitionIndex);
            if (partitionCompare != 0)
            {
                return partitionCompare;
            }

            return left.MemberIndex.CompareTo(right.MemberIndex);
        });

        Dictionary<int, ImmutableArray<RingRange>.Builder> rangesByMemberPartitionBuilders = [];
        for (var i = 0; i < hashIndexPairs.Count; i++)
        {
            var (_, memberIndex, _) = hashIndexPairs[i];
            ref var builder = ref CollectionsMarshal.GetValueRefOrAddDefault(rangesByMemberPartitionBuilders, memberIndex, out _);
            builder ??= ImmutableArray.CreateBuilder<RingRange>(PartitionsPerSilo);
            var (entryStart, _, _) = hashIndexPairs[i];
            var (nextStart, _, _) = hashIndexPairs[(i + 1) % hashIndexPairs.Count];
            var range = (entryStart == nextStart) switch
            {
                true when hashIndexPairs.Count == 1 => RingRange.Full,
                true => RingRange.Empty,
                _ => RingRange.Create(entryStart, nextStart)
            };
            builder.Add(range);
        }

        var rangesByMemberPartition = ImmutableArray.CreateBuilder<ImmutableArray<RingRange>>(sortedActiveMembers.Count);
        for (var i = 0; i < sortedActiveMembers.Count; i++)
        {
            rangesByMemberPartition.Add(rangesByMemberPartitionBuilders[i].ToImmutable());
        }

        _rangesByMemberPartition = rangesByMemberPartition.ToImmutable();

        // Remove empty ranges.
        if (hashIndexPairs.Count > 1)
        {
            for (var i = 1; i < hashIndexPairs.Count;)
            {
                if (hashIndexPairs[i].Hash == hashIndexPairs[i - 1].Hash)
                {
                    hashIndexPairs.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        _ringBoundaries = hashIndexPairs.ToImmutable();

        Members = sortedActiveMembers.ToImmutable();

        _rangesByMember = new RingRangeCollection[Members.Length];
        ClusterMembershipSnapshot = snapshot;
    }

    public static DirectoryMembershipSnapshot Default { get; } = new DirectoryMembershipSnapshot(
        new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue), null!);

    public MembershipVersion Version => ClusterMembershipSnapshot.Version;

    public ImmutableArray<SiloAddress> Members { get; }

    public RingRange GetRange(SiloAddress address, int partitionIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partitionIndex, PartitionsPerSilo - 1);

        var memberIndex = TryGetMemberIndex(address);
        if (memberIndex < 0)
        {
            return RingRange.Empty;
        }

        var ranges = GetMemberRangesByPartition(memberIndex);
        if (partitionIndex >= ranges.Length)
        {
            return RingRange.Empty;
        }

        return ranges[partitionIndex];
    }

    public RingRangeCollection GetMemberRanges(SiloAddress address)
    {
        var memberIndex = TryGetMemberIndex(address);

        if (memberIndex < 0)
        {
            return RingRangeCollection.Empty;
        }

        var range = _rangesByMember[memberIndex];
        if (range.IsDefault)
        {
            range = _rangesByMember[memberIndex] = RingRangeCollection.Create(GetMemberRangesByPartition(memberIndex));
        }

        return range;
    }

    public ImmutableArray<RingRange> GetMemberRangesByPartition(SiloAddress address)
    {
        var memberIndex = TryGetMemberIndex(address);

        if (memberIndex < 0)
        {
            return [];
        }

        return GetMemberRangesByPartition(memberIndex);
    }

    private ImmutableArray<RingRange> GetMemberRangesByPartition(int memberIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memberIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _rangesByMemberPartition.Length);
        return _rangesByMemberPartition[memberIndex];
    }

    public RangeCollection RangeOwners => new(this);

    public ClusterMembershipSnapshot ClusterMembershipSnapshot { get; }

    private (RingRange Range, int MemberIndex, int PartitionIndex) GetRangeInfo(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var range = GetRangeCore(index);
        var boundary = _ringBoundaries[index];
        return (range, boundary.MemberIndex, boundary.PartitionIndex);
    }

    private RingRange GetRangeCore(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var (entryStart, _, _) = _ringBoundaries[index];
        var (nextStart, _, _) = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
        if (entryStart == nextStart)
        {
            // Handle hash collisions by making subsequent adjacent ranges empty.
            if (_ringBoundaries.Length == 1)
            {
                return RingRange.Full;
            }
            else
            {
                // Handle hash collisions by making subsequent adjacent ranges empty.
                return RingRange.Empty;
            }
        }

        return RingRange.Create(entryStart, nextStart);
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
            static (index, state) =>
            {
                var (snapshot, address) = state;
                var candidate = snapshot.Members[index];
                return candidate.CompareTo(address);
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(GrainId grainId, [NotNullWhen(true)] out SiloAddress? owner, [NotNullWhen(true)] out IGrainDirectoryPartition? partitionReference) => TryGetOwner(grainId.GetUniformHashCode(), out owner, out partitionReference);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(uint hashCode, [NotNullWhen(true)] out SiloAddress? owner, [NotNullWhen(true)] out IGrainDirectoryPartition? partitionReference)
    {
        var index = SearchAlgorithms.RingRangeBinarySearch(
            _ringBoundaries.Length,
            this,
            static (collection, index) => collection.GetRangeCore(index),
            hashCode);
        if (index >= 0)
        {
            var (_, memberIndex, partitionIndex) = _ringBoundaries[index];
            owner = Members[memberIndex];
            partitionReference = _partitionsByMember[memberIndex][partitionIndex];
            return true;
        }

        Debug.Assert(Members.Length == 0);
        owner = null;
        partitionReference = null;
        return false;
    }

    public readonly struct RangeCollection(DirectoryMembershipSnapshot snapshot) : IReadOnlyList<(RingRange Range, int MemberIndex, int PartitionIndex)>
    {
        public int Count => snapshot._ringBoundaries.Length;

        public (RingRange Range, int MemberIndex, int PartitionIndex) this[int index] => snapshot.GetRangeInfo(index);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        IEnumerator<(RingRange Range, int MemberIndex, int PartitionIndex)> IEnumerable<(RingRange Range, int MemberIndex, int PartitionIndex)>.GetEnumerator() => GetEnumerator();
        public RangeCollectionEnumerator GetEnumerator() => new(snapshot);

        public struct RangeCollectionEnumerator(DirectoryMembershipSnapshot snapshot) : IEnumerator<(RingRange Range, int MemberIndex, int PartitionIndex)>
        {
            private int _index = 0;
            public readonly (RingRange Range, int MemberIndex, int PartitionIndex) Current => snapshot.GetRangeInfo(_index - 1);
            readonly (RingRange Range, int MemberIndex, int PartitionIndex) IEnumerator<(RingRange Range, int MemberIndex, int PartitionIndex)>.Current => Current;
            readonly object IEnumerator.Current => Current;

            public void Dispose() => _index = int.MaxValue;
            public bool MoveNext()
            {
                if (_index >= 0 && _index++ < snapshot._ringBoundaries.Length)
                {
                    return true;
                }

                return false;
            }

            public void Reset() => _index = 0;
        }
    }
}
