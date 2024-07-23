using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed class DirectoryMembershipSnapshot
{
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

        var memberRanges = ImmutableArray.CreateBuilder<RingRange>(sortedActiveMembers.Count);
        for (var i = 0; i < sortedActiveMembers.Count; i++)
        {
            var memberRange = RingRange.CreateEquallyDividedRange(sortedActiveMembers.Count, i);
            memberRanges.Add(memberRange);
        }

        Members = sortedActiveMembers.ToImmutable();
        Ranges = memberRanges.ToImmutable();
        Debug.Assert(Members.Length == Ranges.Length);

        _snapshot = snapshot;
    }

    public static DirectoryMembershipSnapshot Default { get; } = new DirectoryMembershipSnapshot(
        new ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue));

    public MembershipVersion Version => _snapshot.Version;

    public ImmutableArray<SiloAddress> Members { get; }

    public ImmutableArray<RingRange> Ranges { get; }

    public bool Contains(SiloAddress? address) => TryGetMemberIndex(address) >= 0;

    public RingRange GetRingRange(SiloAddress? address)
    {
        var index = TryGetMemberIndex(address);

        if (index < 0)
        {
            return RingRange.Empty;
        }

        return Ranges[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TryGetMemberIndex(SiloAddress? address)
    {
        if (address is null)
        {
            return -1;
        }

        return BinarySearch(
                Members,
                address,
                static (candidate, address) =>
                {
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

    public bool TryGetOwnerIndex(GrainId grainId, out int owner) => TryGetOwnerIndex(grainId.GetUniformHashCode(), out owner);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetOwnerIndex(uint hashCode, out int index)
    {
        index = BinarySearch(Ranges, hashCode, static (range, hashCode) => range.Compare(hashCode));
        return index >= 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BinarySearch<T, U>(ImmutableArray<T> haystack, U needle, Func<T, U, int> comparer)
    {
        var left = 0;
        var right = haystack.Length - 1;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var comparison = comparer(haystack[mid], needle);

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
}
