using System;
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
    /// A version identifies the same canonical membership view throughout the cluster.
    /// Updates retain versioned fields at the same version and the maximum observed IAmAliveTime for each silo.
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
        public MembershipTableSnapshot(
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.Version = version;
            this.Entries = entries;
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
            return Update(previousSnapshot, version, table.Members.Select(t => t.Item1));
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
            return Update(previousSnapshot, updated.Version, updated.Entries.Values);
        }

        private static MembershipTableSnapshot Update(
            MembershipTableSnapshot previousSnapshot,
            MembershipVersion version,
            IEnumerable<MembershipEntry> updatedEntries)
        {
            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            foreach (var item in updatedEntries)
            {
                var entry = item;
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

            return new MembershipTableSnapshot(version, entries.ToImmutable());
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
        public SiloStatus GetSiloStatus(SiloAddress silo)
        {
            var status = this.Entries.TryGetValue(silo, out var entry) ? entry.Status : SiloStatus.None;
            if (status == SiloStatus.None)
            {
                foreach (var member in this.Entries)
                {
                    if (member.Key.IsSuccessorOf(silo))
                    {
                        status = SiloStatus.Dead;
                        break;
                    }
                }
            }

            return status;
        }

        /// <summary>
        /// Determines whether this snapshot is a successor to another snapshot.
        /// </summary>
        /// <remarks>
        /// At the same canonical membership version, progress consists of newer liveness timestamps
        /// or completed defunct-entry cleanup.
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

            if (Entries.Count > other.Entries.Count)
            {
                // Something is amiss.
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
                return heartbeatAdvanced;
            }

            // Cleanup can remove inactive entries without advancing the table version or a heartbeat.
            // Accept that inventory change while retaining every Active entry and the remaining statuses.
            foreach (var (silo, previousEntry) in other.Entries)
            {
                if (previousEntry.Status == SiloStatus.Active && !Entries.ContainsKey(silo))
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[Version: {this.Version}, {this.Entries.Count} silos");
            foreach (var entry in this.Entries) sb.Append($", {entry.Value}");
            sb.Append(']');
            return sb.ToString();
        }

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        {
            var written = 0;

            if (!destination.TryWrite($"[Version: {this.Version}, {this.Entries.Count} silos", out var length))
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
