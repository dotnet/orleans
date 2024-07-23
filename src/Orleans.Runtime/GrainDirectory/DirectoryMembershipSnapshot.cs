using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Orleans.Configuration;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryMembershipSnapshot
{
    private const int HashesPerEntry = ConsistentRingOptions.DEFAULT_NUM_VIRTUAL_RING_BUCKETS;
    private readonly ClusterMembershipSnapshot _snapshot;
    private readonly ImmutableArray<(uint Start, int MemberIndex)> _ringBoundaries;
    private readonly RingRangeCollection[] _rangesByMember;

    public DirectoryMembershipSnapshot(ClusterMembershipSnapshot snapshot) : this(snapshot, static (silo, count) => silo.GetUniformHashCodes(count))
    {
    }

    internal DirectoryMembershipSnapshot(ClusterMembershipSnapshot snapshot, Func<SiloAddress, int, uint[]> getRingBoundaries)
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
        var hashIndexPairs = ImmutableArray.CreateBuilder<(uint Hash, int MemberIndex)>(HashesPerEntry * sortedActiveMembers.Count);
        for(var i = 0; i < sortedActiveMembers.Count; i++)
        {
            var activeMember = sortedActiveMembers[i];
            var hashCodes = getRingBoundaries(activeMember, HashesPerEntry);
            Debug.Assert(hashCodes.Length == HashesPerEntry);
            foreach (var hashCode in hashCodes)
            {
                hashIndexPairs.Add((hashCode, i));
            }
        }

        hashIndexPairs.Sort(static (left, right) =>
        {
            var hashCompare = left.Hash.CompareTo(right.Hash);
            if (hashCompare != 0)
            {
                return hashCompare;
            }

            return left.MemberIndex.CompareTo(right.MemberIndex);
        });

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
        _snapshot = snapshot;
    }

    public static DirectoryMembershipSnapshot Default { get; } = new DirectoryMembershipSnapshot(
        new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue));

    public MembershipVersion Version => _snapshot.Version;

    public ImmutableArray<SiloAddress> Members { get; }

    public RingRangeCollection GetRanges(SiloAddress address)
    {
        var index = TryGetMemberIndex(address);

        if (index < 0)
        {
            return RingRangeCollection.Empty;
        }

        return GetRanges(index);
    }

    private RingRangeCollection GetRanges(int memberIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(memberIndex, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _rangesByMember.Length);

        var range = _rangesByMember[memberIndex];
        if (range.IsDefault)
        {
            var result = ImmutableArray.CreateBuilder<RingRange>(HashesPerEntry);
            for (var i = 0; i < _ringBoundaries.Length; i++)
            {
                if (_ringBoundaries[i].MemberIndex == memberIndex)
                {
                    result.Add(GetRangeCore(i));
                }
            }

            range = _rangesByMember[memberIndex] = new(result.ToImmutable());
        }

        return range;
    }

    public RangeCollection RangeOwners => new(this);

    private (RingRange Range, int OwnerIndex) GetRangeOwner(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var range = GetRangeCore(index);
        return (range, _ringBoundaries[index].MemberIndex);
    }

    private RingRange GetRangeCore(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _ringBoundaries.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var (entryStart, _) = _ringBoundaries[index];
        var (nextStart, _) = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
        if (entryStart == nextStart)
        {
            // Handle hash collisions by making subsequent adjacent ranges empty.
            return _ringBoundaries.Length == 1 ? RingRange.Full : RingRange.Empty;
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
    public bool TryGetOwner(GrainId grainId, [NotNullWhen(true)] out SiloAddress? owner) => TryGetOwner(grainId.GetUniformHashCode(), out owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(uint hashCode, [NotNullWhen(true)] out SiloAddress? owner)
    {
        var index = SearchAlgorithms.RingRangeBinarySearch(
            _ringBoundaries.Length,
            this,
            static (collection, index) => collection.GetRangeCore(index),
            hashCode);
        if (index >= 0)
        {
            owner = Members[_ringBoundaries[index].MemberIndex];
            return true;
        }

        Debug.Assert(Members.Length == 0); 
        owner = null;
        return false;
    }

    public readonly struct RangeCollection(DirectoryMembershipSnapshot snapshot) : IReadOnlyList<(RingRange Range, int OwnerIndex)>
    {
        public int Count => snapshot._ringBoundaries.Length;

        public (RingRange Range, int OwnerIndex) this[int index] => snapshot.GetRangeOwner(index);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        IEnumerator<(RingRange Range, int OwnerIndex)> IEnumerable<(RingRange Range, int OwnerIndex)>.GetEnumerator() => GetEnumerator();
        public RangeCollectionEnumerator GetEnumerator() => new(snapshot);

        public struct RangeCollectionEnumerator(DirectoryMembershipSnapshot snapshot) : IEnumerator<(RingRange Range, int OwnerIndex)>
        {
            private int _index = 0;
            public readonly (RingRange Range, int OwnerIndex) Current => snapshot.GetRangeOwner(_index - 1);
            readonly (RingRange Range, int OwnerIndex) IEnumerator<(RingRange Range, int OwnerIndex)>.Current => Current;
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
