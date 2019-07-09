using System;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// A snapshot of cluster membership from the perspective of the local grain directory.
    /// </summary>
    internal class DirectoryMembershipSnapshot
    {
        private static readonly Func<SiloAddress, string> PrintSiloAddressForStatistics = (SiloAddress addr) => addr is null ? string.Empty : $"{addr.ToLongString()}/{addr.GetConsistentHashCode():X}";
        private static readonly Comparison<SiloAddress> RingComparer = CompareSiloAddressesForRing;
        private readonly ILogger log;
        private readonly ImmutableList<SiloAddress> ring;
        private readonly SiloAddress siloAddress;

        public DirectoryMembershipSnapshot(
            ILogger log,
            SiloAddress siloAddress,
            ClusterMembershipSnapshot clusterMembership)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.siloAddress = siloAddress ?? throw new ArgumentNullException(nameof(siloAddress));
            this.ClusterMembership = clusterMembership ?? throw new ArgumentNullException(nameof(clusterMembership));

            var activeMembers = ImmutableList.CreateBuilder<SiloAddress>();
            
            foreach (var member in clusterMembership.Members)
            {
                if (member.Value.Status == SiloStatus.Active)
                {
                    ++this.ActiveMemberCount;
                    var silo = member.Value.SiloAddress;
                    activeMembers.Add(silo);
                }
            }

            activeMembers.Sort(RingComparer);
            this.ring = activeMembers.ToImmutable();
        }

        internal static int RingSizeStatistic(DirectoryMembershipSnapshot snapshot) => snapshot.ring.Count;

        internal static string RingDetailsStatistic(DirectoryMembershipSnapshot snapshot) => Utils.EnumerableToString(snapshot.ring, PrintSiloAddressForStatistics);

        internal static string RingPredecessorStatistic(DirectoryMembershipSnapshot snapshot) => PrintSiloAddressForStatistics(snapshot.FindPredecessor(snapshot.siloAddress));

        internal static string RingSuccessorStatistic(DirectoryMembershipSnapshot snapshot) => PrintSiloAddressForStatistics(snapshot.FindSuccessor(snapshot.siloAddress));

        private static int CompareSiloAddressesForRing(SiloAddress left, SiloAddress right)
        {
            var leftHash = left.GetConsistentHashCode();
            var rightHash = right.GetConsistentHashCode();
            return leftHash.CompareTo(rightHash);
        }

        /// <summary>
        /// The monotonically increasing membership version associated with this snapshot.
        /// </summary>
        public ClusterMembershipSnapshot ClusterMembership { get; }

        /// <summary>
        /// The number of active silos.
        /// </summary>
        public int ActiveMemberCount { get; }

        /// <summary>
        /// Returns the <see cref="SiloAddress"/> which owns the directory partition of the provided grain.
        /// </summary>
        [Pure]
        internal SiloAddress CalculateGrainDirectoryPartition(GrainId grainId)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget)
            {
                if (log.IsEnabled(LogLevel.Trace))
                {
                    log.LogTrace(
                        "Silo {LocalSilo} looked for a system target {SystemTarget}, returned {ResultSilo}",
                        this.siloAddress,
                        grainId,
                        this.siloAddress);
                }

                // every silo owns its system targets
                return this.siloAddress;
            }

            if (this.ring.Count == 0) return null;

            SiloAddress siloAddress = null;
            int hash = unchecked((int)grainId.GetUniformHashCode());

            // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
            for (var index = this.ring.Count - 1; index >= 0; --index)
            {
                var item = this.ring[index];
                if (item.GetConsistentHashCode() <= hash)
                {
                    siloAddress = item;
                    break;
                }
            }

            if (siloAddress is null)
            {
                // If not found in the traversal, last silo will do (we are on a ring).
                // We checked above to make sure that the list isn't empty, so this should always be safe.
                siloAddress = this.ring[this.ring.Count - 1];
            }

            if (log.IsEnabled(LogLevel.Trace))
            {
                log.LogTrace(
                    "Silo {LocalSilo} calculated directory partition owner silo {Silo} for grain {Grain}: {GrainHash} --> {SiloHash}",
                    this.siloAddress,
                    siloAddress,
                    grainId,
                    hash,
                    siloAddress?.GetConsistentHashCode());
            }

            return siloAddress;
        }

        [Pure]
        public SiloAddress FindSuccessor(SiloAddress silo)
        {
            // There can be no successor if the ring is empty.
            if (this.ring.Count == 0) return null;

            var index = this.FindIndexOrFirstSuccessor(silo);

            // If the silo was found in the ring, the successor is the subsequent silo.
            if (this.ring[index].Equals(silo)) ++index;

            var result = this.ring[index % this.ring.Count];

            // If the silo is the only silo then it has no successor.
            if (result.Equals(silo)) return null;

            return result;
        }

        [Pure]
        public SiloAddress FindPredecessor(SiloAddress silo)
        {
            // There can be no predecessor if the ring is empty.
            if (this.ring.Count == 0) return null;

            var index = this.FindIndexOrFirstSuccessor(silo) - 1;

            // Wrap around to the end.
            if (index < 0) index = this.ring.Count - 1;

            var result = this.ring[index % this.ring.Count];

            // If the silo is the only silo then it has no predecessor.
            if (result.Equals(silo)) return null;

            return result;
        }

        /// <summary>
        /// Returns the position of the given silo in the ring if it exists.
        /// Otherwise returns its first successor if it exists.
        /// Otherwise returns zero.
        /// </summary>
        private int FindIndexOrFirstSuccessor(SiloAddress silo)
        {
            var r = this.ring;
            for (var i = 0; i < r.Count; i++)
            {
                if (r[i].CompareTo(silo) >= 0) return i;
            }

            return 0;
        }

        public string ToDetailedString()
        {
            var sb = new StringBuilder();
            sb.AppendFormat(
                "Silo address is {0}, silo consistent hash is {1:X}.",
                this.siloAddress,
                this.siloAddress.GetConsistentHashCode()).AppendLine();
            sb.AppendLine("Ring is:");
            foreach (var silo in (this).ring)
            {
                sb.AppendFormat("    Silo {0}, consistent hash is {1:X}", silo, silo.GetConsistentHashCode()).AppendLine();
            }

            var predecessor = this.FindPredecessor(this.siloAddress);
            if (predecessor is object)
            {
                sb.Append($"My predecessor: {predecessor}/{predecessor.GetConsistentHashCode():X}---");
                sb.AppendLine();
            }

            var successor = this.FindSuccessor(this.siloAddress);
            if (successor is object)
            {
                sb.Append($"My successor: {successor}/{successor.GetConsistentHashCode():X}---");
            }

            return sb.ToString();
        }
    }
}
