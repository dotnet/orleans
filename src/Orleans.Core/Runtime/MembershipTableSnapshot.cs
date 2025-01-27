using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace Orleans.Runtime
{
    [GenerateSerializer, Immutable]
    internal sealed class MembershipTableSnapshot
    {
        private static readonly MembershipTableSnapshot InitialValue = new(MembershipVersion.MinValue, ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);

        public MembershipTableSnapshot(
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.Version = version;
            this.Entries = entries;
        }

        public static MembershipTableSnapshot Create(MembershipTableData table) => Update(InitialValue, table);

        public static MembershipTableSnapshot Update(MembershipTableSnapshot previousSnapshot, MembershipTableData table)
        {
            ArgumentNullException.ThrowIfNull(previousSnapshot);
            ArgumentNullException.ThrowIfNull(table);
            var version = (table.Version.Version == 0 && table.Version.VersionEtag == "0")
              ? MembershipVersion.MinValue
              : new MembershipVersion(table.Version.Version);
            return Update(previousSnapshot, version, table.Members.Select(t => t.Item1));
        }

        public static MembershipTableSnapshot Update(MembershipTableSnapshot previousSnapshot, MembershipTableSnapshot updated)
        {
            ArgumentNullException.ThrowIfNull(previousSnapshot);
            ArgumentNullException.ThrowIfNull(updated);
            return Update(previousSnapshot, updated.Version, updated.Entries.Values);
        }

        private static MembershipTableSnapshot Update(MembershipTableSnapshot previousSnapshot, MembershipVersion version, IEnumerable<MembershipEntry> updatedEntries)
        {
            ArgumentNullException.ThrowIfNull(previousSnapshot);
            ArgumentNullException.ThrowIfNull(updatedEntries);

            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            foreach (var item in updatedEntries)
            {
                var entry = item;
                entry = PreserveIAmAliveTime(previousSnapshot, entry);
                entries.Add(entry.SiloAddress, entry);
            }

            return new MembershipTableSnapshot(version, entries.ToImmutable());
        }

        private static MembershipEntry PreserveIAmAliveTime(MembershipTableSnapshot previousSnapshot, MembershipEntry entry)
        {
            // Retain the maximum IAmAliveTime, since IAmAliveTime updates do not increase membership version
            // and therefore can be clobbered by torn reads.
            if (previousSnapshot.Entries.TryGetValue(entry.SiloAddress, out var previousEntry)
                && previousEntry.IAmAliveTime > entry.IAmAliveTime)
            {
                entry = entry.WithIAmAliveTime(previousEntry.IAmAliveTime);
            }

            return entry;
        }

        [Id(0)]
        public MembershipVersion Version { get; }
        
        [Id(1)]
        public ImmutableDictionary<SiloAddress, MembershipEntry> Entries { get; }

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

            foreach (var entry in Entries)
            {
                if (!other.Entries.TryGetValue(entry.Key, out var otherEntry))
                {
                    // Something is amiss.
                    return false;
                }
            }

            // This is a successor if any silo has a later EffectiveIAmAliveTime.
            foreach (var entry in Entries)
            {
                if (entry.Value.EffectiveIAmAliveTime > other.Entries[entry.Key].EffectiveIAmAliveTime)
                {
                    return true;
                }
            }

            return false;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"[Version: {this.Version}, {this.Entries.Count} silos");
            foreach (var entry in this.Entries) sb.Append($", {entry.Value}");
            sb.Append(']');
            return sb.ToString();
        }
    }
}
