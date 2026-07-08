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
        _allMembers = new MemberSet(AllMembers, nameof(allMembers));
        ValidateActiveMembers(ActiveMembers, _allMembers);
        _activeMembers = new MemberSet(ActiveMembers, nameof(activeMembers));
    }

    public MembershipVersion MembershipVersion { get; }

    public ImmutableArray<SiloAddress> AllMembers { get; }

    public ImmutableArray<SiloAddress> ActiveMembers { get; }

    public bool ContainsMember(SiloAddress silo, DisseminationGroup membershipScope = DisseminationGroup.AllMembers) =>
        GetMemberSet(membershipScope).Contains(silo);

    public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        DisseminationGroup membershipScope,
        SiloAddress originator,
        Func<int, int> selectFanOut) =>
        GetMemberSet(membershipScope).GetOriginatorTreeTargets(originator, selectFanOut);

    public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        DisseminationGroup membershipScope,
        SiloAddress localSilo,
        SiloAddress originator,
        SiloAddress sender,
        Func<int, int> selectFanOut) =>
        GetMemberSet(membershipScope).GetForwardingTreeTargets(localSilo, originator, sender, selectFanOut);

    public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
        DisseminationGroup membershipScope,
        SiloAddress localSilo,
        int peerCount) =>
        GetMemberSet(membershipScope).SelectAntiEntropyPeers(localSilo, peerCount);

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

        public MemberSet(ImmutableArray<SiloAddress> members, string parameterName)
        {
            _members = members.IsDefault ? [] : members;
            var indices = new Dictionary<SiloAddress, int>(_members.Length);
            for (var i = 0; i < _members.Length; i++)
            {
                if (!indices.TryAdd(_members[i], i))
                {
                    throw new ArgumentException("Membership snapshot members must be unique.", parameterName);
                }
            }

            // Member arrays are already in tree order. The index map preserves that order for O(1) lookup.
            _indices = indices.ToFrozenDictionary();
        }

        public bool Contains(SiloAddress silo) => _indices.ContainsKey(silo);

        public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(SiloAddress originator, Func<int, int> selectFanOut)
        {
            if (!_indices.TryGetValue(originator, out var originatorIndex))
            {
                return [];
            }

            var fanout = GetFanOut(selectFanOut);
            var result = new List<SiloAddress>(Math.Min(fanout * 2, _members.Length));
            AddTopLevelTargets(fanout, originator, result);
            AddFixedChildren(originatorIndex, fanout, originator, except: null, result);
            return result;
        }

        public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
            SiloAddress localSilo,
            SiloAddress originator,
            SiloAddress sender,
            Func<int, int> selectFanOut)
        {
            if (!_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var fanout = GetFanOut(selectFanOut);
            var result = new List<SiloAddress>(Math.Min(fanout, _members.Length));
            AddFixedChildren(localIndex, fanout, originator, sender, result);
            return result;
        }

        public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
            SiloAddress localSilo,
            int peerCount)
        {
            if (!_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var candidates = new SiloAddress[_members.Length - 1];
            var candidateIndex = 0;
            for (var i = 0; i < _members.Length; i++)
            {
                if (i != localIndex)
                {
                    candidates[candidateIndex++] = _members[i];
                }
            }

            var count = Math.Min(peerCount, candidates.Length);
            if (count <= 0)
            {
                return [];
            }

            var result = ImmutableArray.CreateBuilder<SiloAddress>(count);
            for (var i = 0; i < count; i++)
            {
                var index = Random.Shared.Next(i, candidates.Length);
                if (index != i)
                {
                    (candidates[i], candidates[index]) = (candidates[index], candidates[i]);
                }

                result.Add(candidates[i]);
            }

            return result.MoveToImmutable();
        }

        private void AddTopLevelTargets(
            int fanout,
            SiloAddress originator,
            List<SiloAddress> result)
        {
            var count = Math.Min(fanout, _members.Length);
            for (var i = 0; i < count; i++)
            {
                AddTarget(_members[i], originator, except: null, result);
            }
        }

        private void AddFixedChildren(
            int index,
            int fanout,
            SiloAddress originator,
            SiloAddress? except,
            List<SiloAddress> result)
        {
            var firstChild = GetFirstChildIndex(index, fanout);
            for (var i = 0; i < fanout; i++)
            {
                var childIndex = firstChild + i;
                if (childIndex >= _members.Length)
                {
                    break;
                }

                AddTarget(_members[(int)childIndex], originator, except, result);
            }
        }

        private static void AddTarget(SiloAddress peer, SiloAddress originator, SiloAddress? except, List<SiloAddress> result)
        {
            if (Equals(peer, originator) || except != null && Equals(peer, except))
            {
                return;
            }

            result.Add(peer);
        }

        private int GetFanOut(Func<int, int> selectFanOut) =>
            _members.Length <= 1 ? 1 : Math.Clamp(selectFanOut(_members.Length), 1, _members.Length);

        private static long GetFirstChildIndex(int index, int fanout) =>
            (long)fanout * (index + 1);
    }
}
