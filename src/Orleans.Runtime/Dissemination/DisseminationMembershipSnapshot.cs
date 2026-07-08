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

    public bool ContainsParticipant(SiloAddress silo, DisseminationMembershipScope membershipScope = DisseminationMembershipScope.AllMembers) =>
        GetMemberSet(membershipScope).Contains(silo);

    public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress root,
        Func<int, int> selectFanOut) =>
        GetMemberSet(membershipScope).GetOriginatorTreeTargets(root, selectFanOut);

    public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        SiloAddress root,
        SiloAddress sender,
        Func<int, int> selectFanOut) =>
        GetMemberSet(membershipScope).GetForwardingTreeTargets(localSilo, root, sender, selectFanOut);

    public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        int peerCount) =>
        GetMemberSet(membershipScope).SelectAntiEntropyPeers(localSilo, peerCount);

    private MemberSet GetMemberSet(DisseminationMembershipScope membershipScope) =>
        membershipScope == DisseminationMembershipScope.AllMembers ? _allMembers : _activeMembers;

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
        private readonly ImmutableArray<SiloAddress> _participants;
        private readonly FrozenDictionary<SiloAddress, int> _indices;

        public MemberSet(ImmutableArray<SiloAddress> participants, string parameterName)
        {
            _participants = participants.IsDefault ? [] : participants;
            var indices = new Dictionary<SiloAddress, int>(_participants.Length);
            for (var i = 0; i < _participants.Length; i++)
            {
                if (!indices.TryAdd(_participants[i], i))
                {
                    throw new ArgumentException("Membership snapshot participants must be unique.", parameterName);
                }
            }

            // Participant arrays are already in tree order. The index map preserves that order for O(1) lookup.
            _indices = indices.ToFrozenDictionary();
        }

        public bool Contains(SiloAddress silo) => _indices.ContainsKey(silo);

        public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(SiloAddress root, Func<int, int> selectFanOut)
        {
            if (!_indices.TryGetValue(root, out var rootIndex))
            {
                return [];
            }

            var fanout = GetFanOut(selectFanOut);
            var result = new List<SiloAddress>(Math.Min(fanout * 2, _participants.Length));
            AddTopLevelTargets(fanout, root, result);
            AddFixedChildren(rootIndex, fanout, root, except: null, result);
            return result;
        }

        public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
            SiloAddress localSilo,
            SiloAddress root,
            SiloAddress sender,
            Func<int, int> selectFanOut)
        {
            if (!_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var fanout = GetFanOut(selectFanOut);
            var result = new List<SiloAddress>(Math.Min(fanout, _participants.Length));
            AddFixedChildren(localIndex, fanout, root, sender, result);
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

            var candidates = new SiloAddress[_participants.Length - 1];
            var candidateIndex = 0;
            for (var i = 0; i < _participants.Length; i++)
            {
                if (i != localIndex)
                {
                    candidates[candidateIndex++] = _participants[i];
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
            var firstChild = GetFirstChildIndex(index, fanout);
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
            if (Equals(peer, root) || except != null && Equals(peer, except))
            {
                return;
            }

            result.Add(peer);
        }

        private int GetFanOut(Func<int, int> selectFanOut) =>
            _participants.Length <= 1 ? 1 : Math.Clamp(selectFanOut(_participants.Length), 1, _participants.Length);

        private static long GetFirstChildIndex(int index, int fanout) =>
            (long)fanout * (index + 1);
    }
}
