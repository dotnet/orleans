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
        private readonly FrozenDictionary<SiloAddress, int> _indices;
        private readonly SiloAddress _localSilo;
        private readonly int _localIndex;
        private readonly int _fanout;
        private readonly SiloAddress[] _peers;
        private readonly object _antiEntropyLock = new();

        public MemberSet(
            ImmutableArray<SiloAddress> members,
            SiloAddress localSilo,
            DisseminationOverlayOptions overlayOptions,
            string parameterName)
        {
            _members = members.IsDefault ? [] : members;
            _localSilo = localSilo;
            _fanout = _members.Length <= 1 ? 1 : overlayOptions.GetFanOutFactor(_members.Length);
            var indices = new Dictionary<SiloAddress, int>(_members.Length);
            var localIndex = -1;
            for (var i = 0; i < _members.Length; i++)
            {
                var member = _members[i];
                if (!indices.TryAdd(member, i))
                {
                    throw new ArgumentException("Membership snapshot members must be unique.", parameterName);
                }

                if (Equals(member, localSilo))
                {
                    localIndex = i;
                }
            }

            _localIndex = localIndex;
            // Member arrays are already in tree order. The index map preserves that order for O(1) lookup.
            _indices = indices.ToFrozenDictionary();
            ForwardingTreeTargets = _localIndex < 0 ? [] : ComputeForwardingTreeTargets(_localIndex);

            if (_localIndex < 0)
            {
                _peers = [];
            }
            else
            {
                _peers = new SiloAddress[_members.Length - 1];
                var candidateIndex = 0;
                for (var i = 0; i < _members.Length; i++)
                {
                    if (i != _localIndex)
                    {
                        _peers[candidateIndex++] = _members[i];
                    }
                }
            }

            OriginatorTreeTargets = ComputeOriginatorTreeTargets();
        }

        public bool Contains(SiloAddress silo) => _indices.ContainsKey(silo);

        public ImmutableArray<SiloAddress> OriginatorTreeTargets { get; }

        public ImmutableArray<SiloAddress> ForwardingTreeTargets { get; }

        private ImmutableArray<SiloAddress> ComputeOriginatorTreeTargets()
        {
            if (_localIndex < 0)
            {
                return [];
            }

            var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(_fanout * 2, _members.Length));
            var count = Math.Min(_fanout, _members.Length);
            for (var i = 0; i < count; i++)
            {
                var member = _members[i];
                if (!Equals(member, _localSilo))
                {
                    result.Add(member);
                }
            }

            result.AddRange(ForwardingTreeTargets);
            return result.ToImmutable();
        }

        public void SelectRandomPeers(ref Span<SiloAddress> peers)
        {
            var candidates = _peers;
            var count = Math.Min(peers.Length, candidates.Length);
            if (count <= 0)
            {
                peers = peers[..0];
                return;
            }

            lock (_antiEntropyLock)
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

        private ImmutableArray<SiloAddress> ComputeForwardingTreeTargets(int index)
        {
            var result = ImmutableArray.CreateBuilder<SiloAddress>(Math.Min(_fanout, _members.Length));
            var firstChild = (long)_fanout * (index + 1);
            for (var i = 0; i < _fanout; i++)
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
