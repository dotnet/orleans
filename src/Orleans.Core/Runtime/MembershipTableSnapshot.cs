using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Orleans.Runtime
{
    /// <summary>
    /// Represents an immutable snapshot of a canonical membership view and per-silo liveness timestamps.
    /// </summary>
    /// <remarks>
    /// A version identifies the same canonical membership rows throughout the cluster.
    /// Updates retain versioned fields at the same version and the maximum observed IAmAliveTime for each silo.
    /// Each peer retains local status knowledge separately from those rows.
    /// </remarks>
    [GenerateSerializer, Immutable]
    internal sealed class MembershipTableSnapshot : ISpanFormattable
    {
        private static readonly MembershipTableSnapshot InitialValue = new(MembershipVersion.MinValue, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);

        /// <summary>
        /// Initializes a new instance of the <see cref="MembershipTableSnapshot"/> class.
        /// </summary>
        /// <param name="version">The membership version represented by this snapshot.</param>
        /// <param name="entries">The membership entries contained in this snapshot.</param>
        /// <param name="minimumSiloGeneration">The local exclusive upper bound of retired positive silo generations.</param>
        /// <param name="recentlyDeadSilos">Known dead identities whose records have been removed.</param>
        public MembershipTableSnapshot(
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries,
            int minimumSiloGeneration = 0,
            IReadOnlySet<SiloAddress>? recentlyDeadSilos = null)
        {
            this.Version = version;
            this.Entries = entries;
            this.MinimumSiloGeneration = minimumSiloGeneration;
            // Freeze caller-owned sets so published snapshots remain immutable.
            this.RecentlyDeadSilos = recentlyDeadSilos?.ToFrozenSet() ?? FrozenSet<SiloAddress>.Empty;
        }

        /// <summary>
        /// Creates an initial snapshot from membership table data.
        /// </summary>
        /// <param name="table">The membership table data.</param>
        /// <returns>A snapshot containing the provided table data.</returns>
        public static MembershipTableSnapshot Create(MembershipTableData table) => Update(InitialValue, table);

        /// <summary>
        /// Creates a snapshot by applying membership table data to a previous snapshot.
        /// </summary>
        /// <param name="previousSnapshot">The previous snapshot.</param>
        /// <param name="table">The updated membership table data.</param>
        /// <returns>The resulting membership snapshot.</returns>
        public static MembershipTableSnapshot Update(MembershipTableSnapshot previousSnapshot, MembershipTableData table)
        {
            ArgumentNullException.ThrowIfNull(previousSnapshot);
            ArgumentNullException.ThrowIfNull(table);
            var version = (table.Version.Version == 0 && table.Version.VersionEtag == "0")
              ? MembershipVersion.MinValue
              : new MembershipVersion(table.Version.Version);
            return Update(previousSnapshot, version, table.Members.Select(t => t.Item1), FrozenSet<SiloAddress>.Empty);
        }

        /// <summary>
        /// Creates a snapshot by applying the contents of a newer snapshot to a previous snapshot.
        /// </summary>
        /// <param name="previousSnapshot">The previous snapshot.</param>
        /// <param name="updated">The updated snapshot.</param>
        /// <returns>The resulting membership snapshot.</returns>
        public static MembershipTableSnapshot Update(MembershipTableSnapshot previousSnapshot, MembershipTableSnapshot updated)
        {
            ArgumentNullException.ThrowIfNull(previousSnapshot);
            ArgumentNullException.ThrowIfNull(updated);
            return Update(previousSnapshot, updated.Version, updated.Entries.Values, updated.RecentlyDeadSilos);
        }

        private static MembershipTableSnapshot Update(
            MembershipTableSnapshot previousSnapshot,
            MembershipVersion version,
            IEnumerable<MembershipEntry> updatedEntries,
            IReadOnlySet<SiloAddress> recentlyDeadSilos)
        {
            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            var dead = new HashSet<SiloAddress>(previousSnapshot.RecentlyDeadSilos);
            dead.UnionWith(recentlyDeadSilos);
            foreach (var item in updatedEntries)
            {
                var entry = item;
                if (version == previousSnapshot.Version
                    && entry.Status == SiloStatus.Dead
                    && !previousSnapshot.Entries.ContainsKey(entry.SiloAddress))
                {
                    // An equal-version read can predate pruning of a Dead row.
                    dead.Add(entry.SiloAddress);
                    continue;
                }

                if (previousSnapshot.Entries.TryGetValue(entry.SiloAddress, out var previousEntry))
                {
                    var iAmAliveTime = entry.IAmAliveTime > previousEntry.IAmAliveTime
                        ? entry.IAmAliveTime
                        : previousEntry.IAmAliveTime;
                    if (version == previousSnapshot.Version)
                    {
                        entry = previousEntry;
                    }

                    if (entry.IAmAliveTime < iAmAliveTime)
                    {
                        entry = entry.WithIAmAliveTime(iAmAliveTime);
                    }
                }

                entries.Add(entry.SiloAddress, entry);
            }

            var incomingEntries = entries.ToImmutable();
            var incomingIsOlder = version < previousSnapshot.Version;
            var canonicalEntries = incomingIsOlder ? previousSnapshot.Entries : incomingEntries;
            var olderEntries = incomingIsOlder ? incomingEntries : previousSnapshot.Entries;
            foreach (var (silo, entry) in olderEntries)
            {
                // Delayed reads and successful write completions can reveal a registration
                // which was already removed from the published, newer view.
                if (!canonicalEntries.ContainsKey(silo)
                    && (version != previousSnapshot.Version || entry.Status == SiloStatus.Dead))
                {
                    dead.Add(silo);
                }
            }

            dead.RemoveWhere(silo => canonicalEntries.ContainsKey(silo)
                || IsGenerationRetired(silo, previousSnapshot.MinimumSiloGeneration));
            var retainedDeaths = dead.SetEquals(previousSnapshot.RecentlyDeadSilos)
                ? previousSnapshot.RecentlyDeadSilos
                : dead.ToFrozenSet();
            return new MembershipTableSnapshot(
                incomingIsOlder ? previousSnapshot.Version : version,
                canonicalEntries,
                previousSnapshot.MinimumSiloGeneration,
                retainedDeaths);
        }

        internal MembershipTableSnapshot RetireGenerations(DateTime utcNow, TimeSpan? retirementAge)
        {
            if (retirementAge is not { } age)
            {
                return this;
            }

            var minimum = Math.Max(MinimumSiloGeneration, SiloAddress.GetGenerationRetirementFloor(utcNow, age));
            if (minimum == MinimumSiloGeneration)
            {
                return this;
            }

            var dead = RecentlyDeadSilos;
            if (dead.Any(silo => IsGenerationRetired(silo, minimum)))
            {
                dead = dead.Where(silo => !IsGenerationRetired(silo, minimum)).ToFrozenSet();
            }

            return new(Version, Entries, minimum, dead);
        }

        /// <summary>
        /// Gets the membership version represented by this snapshot.
        /// </summary>
        [Id(0)]
        public MembershipVersion Version { get; }

        /// <summary>
        /// Gets the membership entries contained in this snapshot.
        /// </summary>
        [Id(1)]
        public ImmutableDictionary<SiloAddress, MembershipEntry> Entries { get; }

        /// <summary>
        /// Gets this peer's exclusive upper bound of retired positive silo generations.
        /// </summary>
        [Id(2)]
        public int MinimumSiloGeneration { get; }

        /// <summary>
        /// Gets known dead identities retained separately from the canonical membership rows.
        /// </summary>
        [Id(3)]
        public IReadOnlySet<SiloAddress> RecentlyDeadSilos { get; }

        /// <summary>
        /// Gets the number of active silos in this snapshot.
        /// </summary>
        public int ActiveNodeCount
        {
            get
            {
                var count = 0;
                foreach (var entry in this.Entries)
                {
                    if (entry.Value.Status == SiloStatus.Active)
                    {
                        ++count;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Gets the status of the specified silo in this snapshot.
        /// </summary>
        /// <param name="silo">The silo address.</param>
        /// <returns>The silo status.</returns>
        public SiloStatus GetSiloStatus(SiloAddress silo) => GetSiloStatus(silo, includeRetirement: true);

        internal SiloStatus GetSiloStatus(SiloAddress silo, bool includeRetirement)
        {
            return this.Entries.TryGetValue(silo, out var entry)
                ? entry.Status
                : GetMissingSiloStatus(silo, includeRetirement ? MinimumSiloGeneration : 0, RecentlyDeadSilos, Entries.Keys);
        }

        internal static SiloStatus GetMissingSiloStatus(
            SiloAddress silo,
            int minimumSiloGeneration,
            IReadOnlySet<SiloAddress> recentlyDeadSilos,
            IEnumerable<SiloAddress> members)
        {
            if (IsGenerationRetired(silo, minimumSiloGeneration) || recentlyDeadSilos.Contains(silo))
            {
                return SiloStatus.Dead;
            }

            foreach (var member in members)
            {
                if (member.IsSuccessorOf(silo))
                {
                    return SiloStatus.Dead;
                }
            }

            foreach (var member in recentlyDeadSilos)
            {
                if (member.IsSuccessorOf(silo))
                {
                    return SiloStatus.Dead;
                }
            }

            return SiloStatus.None;
        }

        private static bool IsGenerationRetired(SiloAddress silo, int minimumSiloGeneration) =>
            silo.Generation > 0 && silo.Generation < minimumSiloGeneration;

        /// <summary>
        /// Determines whether this snapshot is a successor to another snapshot.
        /// </summary>
        /// <remarks>
        /// At the same canonical membership version, progress consists of newer liveness timestamps
        /// or pruning previously Dead rows. Non-Dead rows are retained.
        /// </remarks>
        /// <param name="other">The snapshot to compare against.</param>
        /// <returns><see langword="true"/> if this snapshot is a successor to <paramref name="other"/>; otherwise, <see langword="false"/>.</returns>
        public bool IsSuccessorTo(MembershipTableSnapshot other)
        {
            if (Version > other.Version)
            {
                return true;
            }

            if (Version < other.Version)
            {
                return false;
            }

            if (MinimumSiloGeneration < other.MinimumSiloGeneration)
            {
                return false;
            }

            if (Entries.Count > other.Entries.Count)
            {
                // Adding a row requires a new canonical membership version.
                return false;
            }

            var heartbeatAdvanced = false;
            foreach (var (silo, entry) in Entries)
            {
                if (!other.Entries.TryGetValue(silo, out var otherEntry))
                {
                    // Membership changes require a table-version advance.
                    return false;
                }

                heartbeatAdvanced |= entry.IAmAliveTime > otherEntry.IAmAliveTime;
            }

            if (Entries.Count == other.Entries.Count)
            {
                return heartbeatAdvanced
                    || MinimumSiloGeneration > other.MinimumSiloGeneration
                    || !RecentlyDeadSilos.SetEquals(other.RecentlyDeadSilos);
            }

            foreach (var (silo, previousEntry) in other.Entries)
            {
                if (previousEntry.Status != SiloStatus.Dead && !Entries.ContainsKey(silo))
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[Version: {this.Version}, MinimumSiloGeneration: {MinimumSiloGeneration}, {RecentlyDeadSilos.Count} retained deaths, {this.Entries.Count} silos");
            foreach (var entry in this.Entries) sb.Append($", {entry.Value}");
            sb.Append(']');
            return sb.ToString();
        }

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var written = 0;

            if (!destination.TryWrite($"[Version: {this.Version}, MinimumSiloGeneration: {MinimumSiloGeneration}, {RecentlyDeadSilos.Count} retained deaths, {this.Entries.Count} silos", out var length))
            {
                goto fail;
            }

            Advance(ref destination, ref written, length);

            foreach (var entry in this.Entries)
            {
                if (!destination.TryWrite($", {entry.Value}", out length))
                {
                    goto fail;
                }

                Advance(ref destination, ref written, length);
            }

            if (!Append(ref destination, ref written, "]"))
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
