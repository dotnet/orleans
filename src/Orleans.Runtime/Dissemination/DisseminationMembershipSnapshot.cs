using System.Collections.Frozen;
using System.Collections.Immutable;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationMembershipSnapshot
{
    private readonly MemberSet _allMembers;
    private readonly MemberSet _activeMembers;

    public DisseminationMembershipSnapshot(
        MembershipVersion membershipVersion,
        SiloAddress localSilo,
        ImmutableArray<SiloAddress> allMembers,
        ImmutableArray<SiloAddress> activeMembers,
        DisseminationOverlayOptions overlayOptions)
    {
        MembershipVersion = membershipVersion;
        AllMembers = allMembers.IsDefault ? [] : allMembers;
        ActiveMembers = activeMembers.IsDefault ? [] : activeMembers;
        _allMembers = new MemberSet(AllMembers, localSilo, overlayOptions, nameof(allMembers));
        ValidateActiveMembers(ActiveMembers, _allMembers);
        _activeMembers = new MemberSet(ActiveMembers, localSilo, overlayOptions, nameof(activeMembers));
    }

    public MembershipVersion MembershipVersion { get; }

    public ImmutableArray<SiloAddress> AllMembers { get; }

    public ImmutableArray<SiloAddress> ActiveMembers { get; }

    public bool ContainsMember(SiloAddress silo, DisseminationGroup membershipScope = DisseminationGroup.AllMembers) =>
        GetMemberSet(membershipScope).Contains(silo);

    public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        DisseminationGroup membershipScope) =>
        GetMemberSet(membershipScope).OriginatorTreeTargets;

    public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        DisseminationGroup membershipScope) =>
        GetMemberSet(membershipScope).ForwardingTreeTargets;

    public void SelectAntiEntropyPeers(
        DisseminationGroup membershipScope,
        ref Span<SiloAddress> peers) =>
        GetMemberSet(membershipScope).SelectRandomPeers(ref peers);

    private MemberSet GetMemberSet(DisseminationGroup membershipScope) =>
        membershipScope == DisseminationGroup.AllMembers ? _allMembers : _activeMembers;

    private static void ValidateActiveMembers(ImmutableArray<SiloAddress> activeMembers, MemberSet allMembers)
    {
        foreach (var activeMember in activeMembers)
        {
            if (!allMembers.Contains(activeMember))
            {
                throw new ArgumentException("Active members must be present in all members.", nameof(activeMembers));
            }
        }
    }

    private sealed class MemberSet
    {
        private readonly ImmutableArray<SiloAddress> _members;
        private readonly FrozenSet<SiloAddress> _set;
        private readonly SiloAddress[] _shuffledPeers;
        private readonly object _shuffledPeersLock = new();

        public MemberSet(
            ImmutableArray<SiloAddress> members,
            SiloAddress localSilo,
            DisseminationOverlayOptions overlayOptions,
            string parameterName)
        {
            _members = members.IsDefault ? [] : members;
            var fanout = _members.Length <= 1 ? 1 : overlayOptions.GetFanOutFactor(_members.Length);
            var memberSet = new HashSet<SiloAddress>(_members.Length);
            var localIndex = -1;
            for (var i = 0; i < _members.Length; i++)
            {
                var member = _members[i];
                if (!memberSet.Add(member))
                {
                    throw new ArgumentException("Membership snapshot members must be unique.", parameterName);
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
                _shuffledPeers = [];
            }
            else
            {
                _shuffledPeers = new SiloAddress[_members.Length - 1];
                var candidateIndex = 0;
                for (var i = 0; i < _members.Length; i++)
                {
                    if (i != localIndex)
                    {
                        _shuffledPeers[candidateIndex++] = _members[i];
                    }
                }
            }

            OriginatorTreeTargets = ComputeOriginatorTreeTargets(localSilo, localIndex, fanout);
        }

        public bool Contains(SiloAddress silo) => _set.Contains(silo);

        public ImmutableArray<SiloAddress> OriginatorTreeTargets { get; }

        public ImmutableArray<SiloAddress> ForwardingTreeTargets { get; }

        private ImmutableArray<SiloAddress> ComputeOriginatorTreeTargets(SiloAddress localSilo, int localIndex, int fanout)
        {
            if (localIndex < 0)
            {
                return [];
            }

            var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(fanout * 2, _members.Length));
            var count = Math.Min(fanout, _members.Length);
            for (var i = 0; i < count; i++)
            {
                var member = _members[i];
                if (!Equals(member, localSilo))
                {
                    result.Add(member);
                }
            }

            result.AddRange(ForwardingTreeTargets);
            return result.ToImmutable();
        }

        public void SelectRandomPeers(ref Span<SiloAddress> peers)
        {
            var candidates = _shuffledPeers;
            var count = Math.Min(peers.Length, candidates.Length);
            if (count <= 0)
            {
                peers = peers[..0];
                return;
            }

            lock (_shuffledPeersLock)
            {
                for (var i = 0; i < count; i++)
                {
                    var index = Random.Shared.Next(i, candidates.Length);
                    if (index != i)
                    {
                        (candidates[i], candidates[index]) = (candidates[index], candidates[i]);
                    }

                    peers[i] = candidates[i];
                }
            }

            peers = peers[..count];
        }

        private ImmutableArray<SiloAddress> ComputeForwardingTreeTargets(int index, int fanout)
        {
            var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(fanout, _members.Length));
            var firstChild = (long)fanout * (index + 1);
            for (var i = 0; i < fanout; i++)
            {
                var childIndex = firstChild + i;
                if (childIndex >= _members.Length)
                {
                    break;
                }

                result.Add(_members[(int)childIndex]);
            }

            return result.ToImmutable();
        }
    }
}
