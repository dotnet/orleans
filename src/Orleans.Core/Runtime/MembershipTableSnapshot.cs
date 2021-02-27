using System;
using System.Collections.Immutable;
using System.Text;

namespace Orleans.Runtime
{
    [GenerateSerializer]
    internal sealed class MembershipTableSnapshot
    {
        public MembershipTableSnapshot(
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.Version = version;
            this.Entries = entries;
        }

        public static MembershipTableSnapshot Create(MembershipEntry localSiloEntry, MembershipTableData table)
        {
            if (table is null) throw new ArgumentNullException(nameof(table));

            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            if (table.Members != null)
            {
                foreach (var item in table.Members)
                {
                    var entry = item.Item1;
                    entries.Add(entry.SiloAddress, entry);
                }
            }

            if (entries.TryGetValue(localSiloEntry.SiloAddress, out var existing))
            {
                entries[localSiloEntry.SiloAddress] = existing.WithStatus(localSiloEntry.Status);
            }
            else
            {
                entries[localSiloEntry.SiloAddress] = localSiloEntry;
            }

            var version = (table.Version.Version == 0 && table.Version.VersionEtag == "0")
                ? MembershipVersion.MinValue
                : new MembershipVersion(table.Version.Version);
            return new MembershipTableSnapshot(version, entries.ToImmutable());
        }

        public static MembershipTableSnapshot Create(MembershipEntry localSiloEntry, MembershipTableSnapshot snapshot)
        {
            if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            if (snapshot.Entries != null)
            {
                foreach (var item in snapshot.Entries)
                {
                    var entry = item.Value;
                    entries.Add(entry.SiloAddress, entry);
                }
            }

            if (entries.TryGetValue(localSiloEntry.SiloAddress, out var existing))
            {
                entries[localSiloEntry.SiloAddress] = existing.WithStatus(localSiloEntry.Status);
            }
            else
            {
                entries[localSiloEntry.SiloAddress] = localSiloEntry;
            }

            return new MembershipTableSnapshot(snapshot.Version, entries.ToImmutable());
        }

        [Id(1)]
        public MembershipVersion Version { get; }
        
        [Id(2)]
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
