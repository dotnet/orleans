namespace Orleans.Runtime
{
    /// <summary>
    /// Aids in the construction of a consistent hash ring by maintaining an up-to-date reference to the next silo in the ring.
    /// </summary>
    internal class SimpleConsistentRingProvider
    {
        private readonly SiloAddress _localSilo;
        private readonly IClusterMembershipService _clusterMembershipService;
        private readonly object _lockObj = new object();
        private VersionedSuccessor _successor = new VersionedSuccessor(MembershipVersion.MinValue, null);

        public SimpleConsistentRingProvider(ILocalSiloDetails localSiloDetails, IClusterMembershipService clusterMembershipService)
        {
            _localSilo = localSiloDetails.SiloAddress;
            _clusterMembershipService = clusterMembershipService;
            FindSuccessor(_clusterMembershipService.CurrentSnapshot);
        }

        /// <summary>
        /// Gets the <see cref="SiloAddress"/> of the active silo with the smallest consistent hash code value which is larger
        /// than this silo's, or if no such silo exists, then the active silo with the absolute smallest consistent hash code,
        /// or <see langword="null"/> if there are no other active silos in the cluster.
        /// </summary>
        public SiloAddress Successor
        {
            get
            {
                var snapshot = _clusterMembershipService.CurrentSnapshot;
                var (successorVersion, successor) = _successor;

                if (successorVersion < snapshot.Version)
                {
                    lock (_lockObj)
                    {
                        successor = FindSuccessor(snapshot);
                        _successor = new VersionedSuccessor(snapshot.Version, successor);
                    }
                }

                return successor;
            }
        }

        private SiloAddress FindSuccessor(ClusterMembershipSnapshot snapshot)
        {
            var (successorVersion, successor) = _successor;
            if (successorVersion >= snapshot.Version)
            {
                return successor;
            }

            // Find the silo with the smallest hashcode which is larger than this silo's.
            (SiloAddress Silo, int HashCode) firstInRing = (default(SiloAddress), int.MaxValue);
            (SiloAddress Silo, int HashCode) candidate = (default(SiloAddress), int.MaxValue);
            var localSiloHashCode = _localSilo.GetConsistentHashCode();
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

                var memberHashCode = member.SiloAddress.GetConsistentHashCode();

                // It is possible that the local silo is last in the ring, therefore we also find the first silo in the ring,
                // which would be the local silo's successor in that case.
                if (memberHashCode < firstInRing.HashCode)
                {
                    firstInRing = (member.SiloAddress, memberHashCode);
                }

                // This member comes before this silo in the ring, but is not the first in the ring.
                if (memberHashCode < localSiloHashCode)
                {
                    continue;
                }

                // This member comes after this silo in the ring, but before the current candidate.
                // Therefore, this member is the new candidate.
                if (memberHashCode < candidate.HashCode)
                {
                    candidate = (member.SiloAddress, memberHashCode);
                }
            }

            // The result is either the silo with the smallest hashcode that is larger than this silo's,
            // or the first silo in the ring, or null in the case that there are no other active silos.
            successor = candidate.Silo ?? firstInRing.Silo;
            return successor;
        }

        private sealed class VersionedSuccessor
        {
            public VersionedSuccessor(MembershipVersion membershipVersion, SiloAddress siloAddress)
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
