using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Orleans.Configuration;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryMembershipSnapshot
{
    private const int HashesPerEntry = 30;
    private readonly ClusterMembershipSnapshot _snapshot;

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
        RawRanges = hashIndexPairs.ToImmutable();

        Members = sortedActiveMembers.ToImmutable();
        Debug.Assert(Members.Length * HashesPerEntry == RawRanges.Length);

        _snapshot = snapshot;
    }

    public static DirectoryMembershipSnapshot Default { get; } = new DirectoryMembershipSnapshot(
        new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue));

    public MembershipVersion Version => _snapshot.Version;

    public ImmutableArray<SiloAddress> Members { get; }

    public RangeCollection RangeOwners => new(this);

    private (RingRange Range, SiloAddress Owner) GetRangeOwner(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RawRanges.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var entry = RawRanges[index];
        var next = RawRanges[(index + 1) % RawRanges.Length];
        var range = RingRange.Create(entry.Start, next.Start);
        return (range, Members[entry.MemberIndex]);
    }

    private RingRange GetRangeCore(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RawRanges.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 0);

        var entry = RawRanges[index];
        var next = RawRanges[(index + 1) % RawRanges.Length];
        var range = RingRange.Create(entry.Start, next.Start);
        return range;
    }

    //public ImmutableArray<RingRange> Ranges { get; }
    private ImmutableArray<(uint Start, int MemberIndex)> RawRanges { get; }

    public bool Contains(SiloAddress? address) => TryGetMemberIndex(address) >= 0;

    public ImmutableArray<RingRange> GetRingRanges(SiloAddress address)
    {
        var index = TryGetMemberIndex(address);

        if (index < 0)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<RingRange>(HashesPerEntry);
        for (var i = 0; i < RawRanges.Length; i++)
        {
            if (RawRanges[i].MemberIndex == index)
            {
                result.Add(GetRangeCore(i));
            }
        }

        return result.ToImmutable();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TryGetMemberIndex(SiloAddress? address)
    {
        if (address is null)
        {
            return -1;
        }

        return BinarySearch(
            Members.Length,
            address,
            static (snapshot, index, address) =>
            {
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

    public bool TryGetOwnerIndex(GrainId grainId, [NotNullWhen(true)] out SiloAddress? owner) => TryGetOwnerIndex(grainId.GetUniformHashCode(), out owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwnerIndex(uint hashCode, [NotNullWhen(true)] out SiloAddress? owner)
    {
        // Binary search with wrap-around to include the last element to handle the case
        // where it wraps around the boundary of the ring.
        var index = BinarySearch(
            RawRanges.Length + 1,
            hashCode,
            static (snapshot, index, hashCode) =>
            {
                if (index == 0)
                {
                    index = snapshot.RawRanges.Length;
                }

                return snapshot.GetRangeCore(index - 1).CompareTo(hashCode);
            });
        if (index >= 0)
        {
            if (index == 0)
            {
                index = RawRanges.Length;
            }

            owner = Members[RawRanges[index - 1].MemberIndex];
            return true;
        }

        owner = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int BinarySearch<TState>(int length, TState state, Func<DirectoryMembershipSnapshot, int, TState, int> comparer)
    {
        var left = 0;
        var right = length - 1;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var comparison = comparer(this, mid, state);

            if (comparison == 0)
            {
                return mid;
            }
            else if (comparison < 0)
            {
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return -1;
    }

    public readonly struct RangeCollection(DirectoryMembershipSnapshot snapshot) : IEnumerable<(RingRange Range, SiloAddress Owner)>, IReadOnlyCollection<(RingRange Range, SiloAddress Owner)>, IReadOnlyList<(RingRange Range, SiloAddress Owner)>
    {
        public int Count => snapshot.RawRanges.Length;

        public (RingRange Range, SiloAddress Owner) this[int index] => snapshot.GetRangeOwner(index);

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        IEnumerator<(RingRange Range, SiloAddress Owner)> IEnumerable<(RingRange Range, SiloAddress Owner)>.GetEnumerator() => GetEnumerator();
        public RangeCollectionEnumerator GetEnumerator() => new(snapshot);

        public struct RangeCollectionEnumerator(DirectoryMembershipSnapshot snapshot) : IEnumerator<(RingRange Range, SiloAddress Owner)>
        {
            private int _index = 0;
            public readonly (RingRange Range, SiloAddress Owner) Current => snapshot.GetRangeOwner(_index - 1);
            readonly (RingRange Range, SiloAddress Owner) IEnumerator<(RingRange Range, SiloAddress Owner)>.Current => Current;
            readonly object IEnumerator.Current => Current;

            public void Dispose() => _index = int.MaxValue;
            public bool MoveNext()
            {
                if (_index >= 0 && _index++ < snapshot.RawRanges.Length)
                {
                    return true;
                }

                return false;
            }

            public void Reset() => _index = 0;
        }
    }
}
