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
    private readonly RingRangeCollection[] _virtualBucketsByMember;

    public DirectoryMembershipSnapshot(ClusterMembershipSnapshot snapshot)
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

        sortedActiveMembers.Sort(static (left, right) => left.GetConsistentHashCode().CompareTo(right.GetConsistentHashCode()));
        var hashIndexPairs = ImmutableArray.CreateBuilder<(uint Hash, int MemberIndex)>(HashesPerEntry * sortedActiveMembers.Count);
        for(var i = 0; i < sortedActiveMembers.Count; i++)
        {
            var activeMember = sortedActiveMembers[i];
            var hashCodes = activeMember.GetUniformHashCodes(HashesPerEntry);
            foreach (var hashCode in hashCodes)
            {
                hashIndexPairs.Add((hashCode, i));
            }
        }

        hashIndexPairs.Sort(static (left, right) => left.Hash.CompareTo(right.Hash));
        _ringBoundaries = hashIndexPairs.ToImmutable();

        Members = sortedActiveMembers.ToImmutable();
        Debug.Assert(Members.Length * HashesPerEntry == _ringBoundaries.Length);

        _virtualBucketsByMember = new RingRangeCollection[Members.Length];
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
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _virtualBucketsByMember.Length);

        var range = _virtualBucketsByMember[memberIndex];
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

            range = _virtualBucketsByMember[memberIndex] = new(result.ToImmutable());
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

        var entry = _ringBoundaries[index];
        var next = _ringBoundaries[(index + 1) % _ringBoundaries.Length];
        if (entry.Start == next.Start)
        {
            // Handle hash collisions by making adjacent ranges empty.
            return _ringBoundaries.Length == 1 ? RingRange.Full : RingRange.Empty;
        }

        return RingRange.Create(entry.Start, next.Start);
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
                var comparison = candidate.GetConsistentHashCode().CompareTo(address.GetConsistentHashCode());
                if (comparison != 0)
                {
                    return comparison;
                }

                if (candidate.Equals(address))
                {
                    return 0;
                }

                return candidate.CompareTo(address);
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(GrainId grainId, [NotNullWhen(true)] out SiloAddress? owner) => TryGetOwner(grainId.GetUniformHashCode(), out owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwner(uint hashCode, [NotNullWhen(true)] out SiloAddress? owner)
    {
        // Binary search with wrap-around to include the last element to handle the case
        // where it wraps around the boundary of the ring.
        if (_ringBoundaries.Length > 0)
        {
            var index = SearchAlgorithms.BinarySearch(
                _ringBoundaries.Length + 1,
                (this, hashCode),
                static (index, state) =>
                {
                    var (snapshot, hashCode) = state;
                    if (index == 0)
                    {
                        index = snapshot._ringBoundaries.Length;
                    }

                    return snapshot.GetRangeCore(index - 1).CompareTo(hashCode);
                });
            if (index >= 0)
            {
                if (index == 0)
                {
                    index = _ringBoundaries.Length;
                }

                owner = Members[_ringBoundaries[index - 1].MemberIndex];
                return true;
            }
        }

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
