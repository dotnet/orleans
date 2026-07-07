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

    public int GetParticipantCount(DisseminationMembershipScope membershipScope, SiloAddress localSilo) =>
        GetMemberSet(membershipScope).GetParticipantCount(localSilo);

    public bool ContainsParticipant(DisseminationMembershipScope membershipScope, SiloAddress silo, SiloAddress localSilo) =>
        GetMemberSet(membershipScope).ContainsParticipant(silo, localSilo);

    public bool ContainsCurrentParticipant(SiloAddress localSilo, SiloAddress peer) =>
        Equals(peer, localSilo) || _allMembers.Contains(peer);

    public IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        SiloAddress root,
        int fanout) =>
        GetMemberSet(membershipScope).IncludeLocal(localSilo).GetOriginatorTreeTargets(root, fanout);

    public static IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(
        IReadOnlyCollection<SiloAddress> targetPeers,
        SiloAddress localSilo,
        SiloAddress root,
        Func<int, int> getFanOutFactor)
    {
        var memberSet = MemberSet.CreateAdHoc(targetPeers, localSilo, root);
        return memberSet.GetOriginatorTreeTargets(root, getFanOutFactor(memberSet.Count));
    }

    public IReadOnlyList<SiloAddress> GetForwardingTreeTargets(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        SiloAddress root,
        SiloAddress sender,
        int fanout) =>
        GetMemberSet(membershipScope).IncludeLocal(localSilo).GetForwardingTreeTargets(localSilo, root, sender, fanout);

    public ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
        DisseminationMembershipScope membershipScope,
        SiloAddress localSilo,
        string topicName,
        long round,
        int fanout,
        int peerCount) =>
        GetMemberSet(membershipScope).IncludeLocal(localSilo).SelectAntiEntropyPeers(localSilo, topicName, round, fanout, peerCount);

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

        public static MemberSet CreateAdHoc(
            IEnumerable<SiloAddress> participants,
            SiloAddress localSilo,
            SiloAddress root)
        {
            var orderedParticipants = new List<SiloAddress>();
            var seen = new HashSet<SiloAddress>();
            foreach (var participant in participants)
            {
                AddParticipant(participant);
            }

            AddParticipant(localSilo);
            AddParticipant(root);
            orderedParticipants.Sort(static (left, right) => left.CompareTo(right));
            return new MemberSet([.. orderedParticipants]);

            void AddParticipant(SiloAddress participant)
            {
                if (seen.Add(participant))
                {
                    orderedParticipants.Add(participant);
                }
            }
        }

        public bool Contains(SiloAddress silo) => _indices.ContainsKey(silo);

        public int GetParticipantCount(SiloAddress localSilo) =>
            _indices.ContainsKey(localSilo) ? _participants.Length : _participants.Length + 1;

        public bool ContainsParticipant(SiloAddress silo, SiloAddress localSilo) =>
            Equals(silo, localSilo) || _indices.ContainsKey(silo);

        public MemberSet IncludeLocal(SiloAddress localSilo)
        {
            if (_indices.ContainsKey(localSilo))
            {
                return this;
            }

            var participants = ImmutableArray.CreateBuilder<SiloAddress>(_participants.Length + 1);
            participants.AddRange(_participants);
            participants.Add(localSilo);
            return new MemberSet(participants.MoveToImmutable());
        }

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
            string topicName,
            long round,
            int fanout,
            int peerCount)
        {
            if (_participants.Length <= 1 || !_indices.TryGetValue(localSilo, out var localIndex))
            {
                return [];
            }

            var candidates = new List<(SiloAddress Peer, ulong Score)>();
            foreach (var index in GetAntiEntropyCandidateIndexes(localIndex, _participants.Length, fanout))
            {
                if (index != localIndex)
                {
                    var peer = _participants[index];
                    candidates.Add((peer, GetRepairPeerScore(peer, topicName, round, localIndex)));
                }
            }

            var count = Math.Min(peerCount, candidates.Count);
            if (count <= 0)
            {
                return [];
            }

            candidates.Sort(static (left, right) =>
            {
                var result = left.Score.CompareTo(right.Score);
                return result != 0 ? result : left.Peer.CompareTo(right.Peer);
            });

            var result = ImmutableArray.CreateBuilder<SiloAddress>(count);
            for (var i = 0; i < count; i++)
            {
                result.Add(candidates[i].Peer);
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
            if (Equals(peer, root) || except is { } excluded && Equals(peer, excluded) || result.Contains(peer))
            {
                return;
            }

            result.Add(peer);
        }

        private static IEnumerable<int> GetAntiEntropyCandidateIndexes(int localIndex, int participantCount, int fanout)
        {
            if (participantCount <= 1)
            {
                yield break;
            }

            var topLevelEnd = Math.Min(fanout, participantCount);
            if (localIndex < topLevelEnd)
            {
                for (var i = 0; i < topLevelEnd; i++)
                {
                    yield return i;
                }

                yield break;
            }

            var parentIndex = localIndex / fanout - 1;
            if (parentIndex < 0)
            {
                yield break;
            }

            var (previousLevelStart, previousLevelEnd) = GetLevelRange(parentIndex, participantCount, fanout);
            var windowStart = previousLevelStart + (parentIndex - previousLevelStart) / fanout * fanout;
            var windowEnd = Math.Min(previousLevelEnd, windowStart + fanout);
            for (var i = windowStart; i < windowEnd; i++)
            {
                yield return i;
            }
        }

        private static (int Start, int End) GetLevelRange(int index, int participantCount, int fanout)
        {
            var start = 0L;
            var width = (long)fanout;
            while (index >= start + width && start + width < participantCount)
            {
                start += width;
                width = Math.Min(width * fanout, participantCount - start);
            }

            return ((int)start, (int)Math.Min(participantCount, start + width));
        }

        private static ulong GetRepairPeerScore(SiloAddress peer, string topicName, long round, int localIndex)
        {
            var value = (ulong)(uint)peer.GetConsistentHashCode();
            value ^= Mix(GetStableStringHash(topicName));
            value ^= (ulong)round * 0x9E3779B97F4A7C15UL;
            value ^= (ulong)(uint)localIndex << 32;
            return Mix(value);
        }

        private static ulong GetStableStringHash(string value)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offsetBasis;
            foreach (var ch in value)
            {
                hash ^= (byte)ch;
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }

            return hash;
        }

        private static ulong Mix(ulong value)
        {
            value ^= value >> 30;
            value *= 0xBF58476D1CE4E5B9UL;
            value ^= value >> 27;
            value *= 0x94D049BB133111EBUL;
            value ^= value >> 31;
            return value;
        }
    }
}
