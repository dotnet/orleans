using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationMembershipSnapshot
{
    private readonly MemberSet _allMembers;
    private readonly MemberSet _activeMembers;

    public DisseminationMembershipSnapshot(
        MembershipVersion membershipVersion,
        ImmutableArray<SiloAddress> allMembers,
        ImmutableArray<SiloAddress> activeMembers)
    {
        MembershipVersion = membershipVersion;
        AllMembers = allMembers.IsDefault ? [] : allMembers;
        ActiveMembers = activeMembers.IsDefault ? [] : activeMembers;
        _allMembers = new MemberSet(AllMembers);
        _activeMembers = AllMembers.SequenceEqual(ActiveMembers) ? _allMembers : new MemberSet(ActiveMembers);
    }

    public MembershipVersion MembershipVersion { get; }

    public ImmutableArray<SiloAddress> AllMembers { get; }

    public ImmutableArray<SiloAddress> ActiveMembers { get; }

    public int GetParticipantCount(DisseminationMembershipScope membershipScope) =>
        GetMemberSet(membershipScope).Count;

    public bool ContainsParticipant(DisseminationMembershipScope membershipScope, SiloAddress silo) =>
        GetMemberSet(membershipScope).Contains(silo);

    public bool ContainsMember(SiloAddress peer) => _allMembers.Contains(peer);

    public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress root,
        int fanout) =>
        GetMemberSet(membershipScope).GetOriginatorTreeTargets(root, fanout);

    public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        SiloAddress root,
        SiloAddress sender,
        int fanout) =>
        GetMemberSet(membershipScope).GetForwardingTreeTargets(localSilo, root, sender, fanout);

    public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        int peerCount) =>
        GetMemberSet(membershipScope).SelectAntiEntropyPeers(localSilo, peerCount);

    private MemberSet GetMemberSet(DisseminationMembershipScope membershipScope) =>
        membershipScope == DisseminationMembershipScope.AllMembers ? _allMembers : _activeMembers;

    private sealed class MemberSet
    {
        private readonly ImmutableArray<SiloAddress> _participants;
        private readonly FrozenDictionary<SiloAddress, int> _indices;

        public MemberSet(ImmutableArray<SiloAddress> participants)
        {
            _participants = participants.IsDefault ? [] : participants;
            var indices = new Dictionary<SiloAddress, int>(_participants.Length);
            for (var i = 0; i < _participants.Length; i++)
            {
                indices[_participants[i]] = i;
            }

            _indices = indices.ToFrozenDictionary();
        }

        public int Count => _participants.Length;

        public bool Contains(SiloAddress silo) => _indices.ContainsKey(silo);

        public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(SiloAddress root, int fanout)
        {
            if (!_indices.TryGetValue(root, out var rootIndex))
            {
                return [];
            }

            var result = new List<SiloAddress>(Math.Min(fanout * 2, _participants.Length));
            AddTopLevelTargets(fanout, root, result);
            AddFixedChildren(rootIndex, fanout, root, except: null, result);
            return result;
        }

        public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
            SiloAddress localSilo,
            SiloAddress root,
            SiloAddress sender,
            int fanout)
        {
            if (!_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var result = new List<SiloAddress>(Math.Min(fanout, _participants.Length));
            AddFixedChildren(localIndex, fanout, root, sender, result);
            return result;
        }

        public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
            SiloAddress localSilo,
            int peerCount)
        {
            if (_participants.Length <= 1 || !_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var count = Math.Min(peerCount, _participants.Length - 1);
            if (count <= 0)
            {
                return [];
            }

            var selected = new HashSet<int>(count);
            var result = ImmutableArray.CreateBuilder<SiloAddress>(count);
            while (result.Count < count)
            {
                var index = Random.Shared.Next(_participants.Length);
                if (index != localIndex && selected.Add(index))
                {
                    result.Add(_participants[index]);
                }
            }

            return result.MoveToImmutable();
        }

        private void AddTopLevelTargets(
            int fanout,
            SiloAddress root,
            List<SiloAddress> result)
        {
            var count = Math.Min(fanout, _participants.Length);
            for (var i = 0; i < count; i++)
            {
                AddTarget(_participants[i], root, except: null, result);
            }
        }

        private void AddFixedChildren(
            int index,
            int fanout,
            SiloAddress root,
            SiloAddress? except,
            List<SiloAddress> result)
        {
            var firstChild = (long)fanout * (index + 1);
            for (var i = 0; i < fanout; i++)
            {
                var childIndex = firstChild + i;
                if (childIndex >= _participants.Length)
                {
                    break;
                }

                AddTarget(_participants[(int)childIndex], root, except, result);
            }
        }

        private static void AddTarget(SiloAddress peer, SiloAddress root, SiloAddress? except, List<SiloAddress> result)
        {
            if (Equals(peer, root) || except != null && Equals(peer, except) || result.Contains(peer))
            {
                return;
            }

            result.Add(peer);
        }
    }
}
