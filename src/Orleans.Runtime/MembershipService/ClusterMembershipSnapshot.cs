using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents a snapshot of cluster membership.
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    public sealed class ClusterMembershipSnapshot : ISpanFormattable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterMembershipSnapshot"/> class.
        /// </summary>
        /// <param name="members">The cluster members.</param>
        /// <param name="version">The cluster membership version.</param>
        public ClusterMembershipSnapshot(ImmutableDictionary<SiloAddress, ClusterMember> members, MembershipVersion version)
            : this(members, version, 0)
        {
        }

        /// <summary>
        /// Initializes a cluster view with local generation-retirement knowledge.
        /// </summary>
        /// <param name="members">The cluster members.</param>
        /// <param name="version">The cluster membership version.</param>
        /// <param name="minimumSiloGeneration">The exclusive upper bound of retired positive silo generations.</param>
        /// <param name="recentlyDeadSilos">Known dead identities whose records have been removed.</param>
        internal ClusterMembershipSnapshot(
            ImmutableDictionary<SiloAddress, ClusterMember> members,
            MembershipVersion version,
            int minimumSiloGeneration,
            IReadOnlySet<SiloAddress>? recentlyDeadSilos = null)
        {
            this.Members = members;
            this.Version = version;
            this.MinimumSiloGeneration = minimumSiloGeneration;
            this.RecentlyDeadSilos = recentlyDeadSilos?.ToFrozenSet() ?? FrozenSet<SiloAddress>.Empty;
        }

        internal static ClusterMembershipSnapshot Default => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, MembershipVersion.MinValue);

        /// <summary>
        /// Gets the cluster members.
        /// </summary>
        /// <value>The cluster members.</value>
        [Id(0)]
        public ImmutableDictionary<SiloAddress, ClusterMember> Members { get; }

        /// <summary>
        /// Gets the cluster membership version.
        /// </summary>
        /// <value>The cluster membership version.</value>
        [Id(1)]
        public MembershipVersion Version { get; }

        /// <summary>
        /// Gets the exclusive upper bound of retired positive silo generations.
        /// Absent silos below this bound are dead; present members retain their recorded status.
        /// </summary>
        [Id(2)]
        internal int MinimumSiloGeneration { get; }

        [Id(3)]
        internal IReadOnlySet<SiloAddress> RecentlyDeadSilos { get; }

        internal bool IsSuccessorTo(ClusterMembershipSnapshot previous)
        {
            if (Version != previous.Version)
            {
                return Version > previous.Version;
            }

            if (MinimumSiloGeneration < previous.MinimumSiloGeneration)
            {
                return false;
            }

            return MinimumSiloGeneration > previous.MinimumSiloGeneration
                || Members.Count < previous.Members.Count
                || RecentlyDeadSilos.IsProperSupersetOf(previous.RecentlyDeadSilos);
        }

        /// <summary>
        /// Gets status of the specified silo.
        /// </summary>
        /// <remarks>
        /// Present members retain their recorded status. Absent identities are classified using
        /// the snapshot's fixed generation cutoff, retained death evidence, and successor incarnations.
        /// </remarks>
        /// <param name="silo">The silo.</param>
        /// <returns>The status of the specified silo.</returns>
        public SiloStatus GetSiloStatus(SiloAddress silo)
        {
            return this.Members.TryGetValue(silo, out var entry)
                ? entry.Status
                : MembershipTableSnapshot.GetMissingSiloStatus(silo, MinimumSiloGeneration, RecentlyDeadSilos, Members.Keys);
        }

        /// <summary>
        /// Gets status of the specified silo, treating unknown silos as dead if this snapshot is newer than when the silo was seen.
        /// </summary>
        /// <param name="silo">The silo.</param>
        /// <param name="seenAtVersion">The membership version when the silo was last seen.</param>
        /// <returns>The status of the specified silo.</returns>
        public SiloStatus GetSiloStatus(SiloAddress silo, MembershipVersion seenAtVersion)
        {
            var status = GetSiloStatus(silo);
            return status == SiloStatus.None && this.Version > seenAtVersion ? SiloStatus.Dead : status;
        }

        /// <summary>
        /// Returns a <see cref="ClusterMembershipUpdate"/> which represents this instance.
        /// </summary>
        /// <returns>A <see cref="ClusterMembershipUpdate"/> which represents this instance.</returns>
        public ClusterMembershipUpdate AsUpdate() => new ClusterMembershipUpdate(this, this.Members.Values.ToImmutableArray());

        /// <summary>
        /// Returns a <see cref="ClusterMembershipUpdate"/> which represents the change in cluster membership from the provided snapshot to this instance.
        /// </summary>
        /// <returns>A <see cref="ClusterMembershipUpdate"/> which represents the change in cluster membership from the provided snapshot to this instance.</returns>
        public ClusterMembershipUpdate CreateUpdate(ClusterMembershipSnapshot previous)
        {
            if (previous is null) throw new ArgumentNullException(nameof(previous));
            if (this.Version < previous.Version)
            {
                throw new ArgumentException($"Argument must have a previous version to the current instance. Expected <= {this.Version}, encountered {previous.Version}", nameof(previous));
            }

            if (this.Version == previous.Version)
            {
                return new ClusterMembershipUpdate(this, ImmutableArray<ClusterMember>.Empty);
            }

            var changes = ImmutableHashSet.CreateBuilder<ClusterMember>();
            foreach (var entry in this.Members)
            {
                // Include any entry which is new or has changed state.
                if (!previous.Members.TryGetValue(entry.Key, out var previousEntry) || previousEntry.Status != entry.Value.Status)
                {
                    changes.Add(entry.Value);
                }
            }

            // Handle entries which were removed entirely.
            foreach (var entry in previous.Members)
            {
                if (!this.Members.TryGetValue(entry.Key, out _))
                {
                    changes.Add(new ClusterMember(entry.Key, SiloStatus.Dead, entry.Value.Name, wasDeclaredDead: true));
                }
            }

            return new ClusterMembershipUpdate(this, changes.ToImmutableArray());
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"ClusterMembershipSnapshot {{ Version = {this.Version}, Members.Count = {this.Members.Count}, Members = [");
            var first = true;
            foreach (var member in this.Members)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    sb.Append(", ");
                }

                sb.Append($"{member.Value}");
            }

            sb.Append("] }}");
            return sb.ToString();
        }

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var written = 0;

            if (!destination.TryWrite($"ClusterMembershipSnapshot {{ Version = {this.Version}, Members.Count = {this.Members.Count}, Members = [", out var length))
            {
                goto fail;
            }

            Advance(ref destination, ref written, length);

            var first = true;
            foreach (var member in this.Members)
            {
                if (first)
                {
                    first = false;
                }
                else if (!Append(ref destination, ref written, ", "))
                {
                    goto fail;
                }

                if (!destination.TryWrite($"{member.Value}", out length))
                {
                    goto fail;
                }

                Advance(ref destination, ref written, length);
            }

            if (!Append(ref destination, ref written, "] }}"))
            {
                goto fail;
            }

            charsWritten = written;
            return true;

fail:
            charsWritten = 0;
            return false;

            static bool Append(ref Span<char> destination, ref int written, ReadOnlySpan<char> value)
            {
                if (!value.TryCopyTo(destination))
                {
                    return false;
                }

                Advance(ref destination, ref written, value.Length);
                return true;
            }

            static void Advance(ref Span<char> destination, ref int written, int length)
            {
                destination = destination[length..];
                written += length;
            }
        }
    }
}
