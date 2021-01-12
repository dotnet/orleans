using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal class SimpleConsistentRingProvider
    {
        private readonly SiloAddress _localSilo;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly object _lockObj = new object();
        private List<SiloAddress> _ring = new List<SiloAddress>(0);
        private SuccessorSnapshot _successor = new SuccessorSnapshot(MembershipVersion.MinValue, null);

        public SimpleConsistentRingProvider(ILocalSiloDetails localSiloDetails, IClusterMembershipService clusterMembershipService)
        {
            _localSilo = localSiloDetails.SiloAddress;
            _clusterMembershipService = clusterMembershipService;
            RebuildRing(_clusterMembershipService.CurrentSnapshot);
        }

        public SiloAddress Successor
        {
            get
            {
                var snapshot = _clusterMembershipService.CurrentSnapshot;
                var (version, successor) = _successor;

                if (!version.Equals(snapshot.Version))
                {
                    RebuildRing(snapshot);
                    (_, successor) = _successor;
                }

                return successor;
            }
        }

        private void RebuildRing(ClusterMembershipSnapshot snapshot)
        {
            lock (_lockObj)
            {
                var (version, _) = _successor;
                if (version.Equals(snapshot.Version))
                {
                    return;
                }

                var tmp = new List<SiloAddress>();
                tmp.Add(_localSilo);

                foreach (var member in snapshot.Members.Values)
                {
                    if (member.SiloAddress.Equals(_localSilo))
                    {
                        continue;
                    }

                    if (member.Status != SiloStatus.Active)
                    {
                        continue;
                    }

                    tmp.Add(member.SiloAddress);
                }

                tmp.Sort((x, y) => x.GetConsistentHashCode().CompareTo(y.GetConsistentHashCode()));

                _ring = tmp;
                _successor = new SuccessorSnapshot(snapshot.Version, FindSuccessor(_ring, _localSilo));
            }

            static SiloAddress FindSuccessor(List<SiloAddress> ring, SiloAddress localSilo)
            {
                if (ring is null || ring.Count == 1)
                {
                    return null;
                }

                var index = -1;
                for (var i = 0; i < ring.Count; i++)
                {
                    if (ring[i].Equals(localSilo))
                    {
                        index = i;
                        break;
                    }
                }

                if (index == -1)
                {
                    return null;
                }

                return ring[(index + 1) % ring.Count];
            }
        }

        private sealed class SuccessorSnapshot
        {
            public SuccessorSnapshot(MembershipVersion membershipVersion, SiloAddress siloAddress)
            {
                MembershipVersion = membershipVersion;
                SiloAddress = siloAddress;
            }

            public void Deconstruct(out MembershipVersion version, out SiloAddress siloAddress)
            {
                version = MembershipVersion;
                siloAddress = SiloAddress;
            }

            public MembershipVersion MembershipVersion { get; }
            public SiloAddress SiloAddress { get; }
        }
    }
}
