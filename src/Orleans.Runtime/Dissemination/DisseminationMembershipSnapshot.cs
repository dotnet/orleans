using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationMembershipSnapshot
{
    private readonly FrozenSet<SiloAddress> _set;
    private readonly SiloAddress[] _antiEntropyPeers;
    private readonly AntiEntropyPeerSelection _antiEntropySelection;

    public DisseminationMembershipSnapshot(
        MembershipVersion membershipVersion,
        SiloAddress localSilo,
        ImmutableArray<SiloAddress> members,
        DisseminationOverlayOptions overlayOptions,
        DisseminationMembershipSnapshot? previous = null)
    {
        MembershipVersion = membershipVersion;
        Members = members.IsDefault ? [] : members;
        // Membership versions also advance without changing the eligible peers. Preserve progress so that
        // frequent updates cannot keep repair rounds confined to the first few members.
        _antiEntropySelection = previous?._antiEntropySelection ?? new();
        var fanout = Members.Length <= 1 ? 1 : overlayOptions.GetFanOutFactor(Members.Length);
        var memberSet = new HashSet<SiloAddress>(Members.Length);
        var localIndex = -1;
        for (var i = 0; i < Members.Length; i++)
        {
            var member = Members[i];
            if (!memberSet.Add(member))
            {
                throw new ArgumentException("Membership snapshot members must be unique.", nameof(members));
            }

            if (Equals(member, localSilo))
            {
                localIndex = i;
            }
        }

        _set = memberSet.ToFrozenSet();
        ForwardingTreeTargets = localIndex < 0 ? [] : ComputeForwardingTreeTargets(localIndex, fanout);

        if (localIndex < 0)
        {
            _antiEntropyPeers = [];
        }
        else
        {
            _antiEntropyPeers = new SiloAddress[Members.Length - 1];
            var candidateIndex = 0;
            for (var i = 0; i < Members.Length; i++)
            {
                if (i != localIndex)
                {
                    _antiEntropyPeers[candidateIndex++] = Members[i];
                }
            }
        }

        OriginatorTreeTargets = ComputeOriginatorTreeTargets(localSilo, localIndex, fanout);
    }

    public MembershipVersion MembershipVersion { get; }

    public ImmutableArray<SiloAddress> Members { get; }

    public ImmutableArray<SiloAddress> OriginatorTreeTargets { get; }

    public ImmutableArray<SiloAddress> ForwardingTreeTargets { get; }

    public bool ContainsMember(SiloAddress silo) => _set.Contains(silo);

    public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(int peerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(peerCount);
        var candidates = _antiEntropyPeers;
        var count = Math.Min(peerCount, candidates.Length);
        if (count <= 0)
        {
            return [];
        }

        var peers = new SiloAddress[count];
        lock (_antiEntropySelection)
        {
            for (var i = 0; i < count; i++)
            {
                peers[i] = candidates[(_antiEntropySelection.Cursor + i) % candidates.Length];
            }

            _antiEntropySelection.Cursor = (_antiEntropySelection.Cursor + count) % candidates.Length;
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(peers);
    }

    private ImmutableArray<SiloAddress> ComputeOriginatorTreeTargets(SiloAddress localSilo, int localIndex, int fanout)
    {
        if (localIndex < 0)
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(fanout * 2, Members.Length));
        var count = Math.Min(fanout, Members.Length);
        for (var i = 0; i < count; i++)
        {
            var member = Members[i];
            if (!Equals(member, localSilo))
            {
                result.Add(member);
            }
        }

        result.AddRange(ForwardingTreeTargets);
        return result.ToImmutable();
    }

    private ImmutableArray<SiloAddress> ComputeForwardingTreeTargets(int index, int fanout)
    {
        var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(fanout, Members.Length));
        var firstChild = (long)fanout * (index + 1);
        for (var i = 0; i < fanout; i++)
        {
            var childIndex = firstChild + i;
            if (childIndex >= Members.Length)
            {
                break;
            }

            result.Add(Members[(int)childIndex]);
        }

        return result.ToImmutable();
    }

    private sealed class AntiEntropyPeerSelection
    {
        public int Cursor;
    }
}
