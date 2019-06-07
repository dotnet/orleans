using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService
{
    internal sealed class MembershipTableSnapshot
    {
        public MembershipTableSnapshot(
            MembershipEntry localSilo,
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.LocalSilo = localSilo;
            this.Version = version;
            this.Entries = entries;
        }

        public static MembershipTableSnapshot Create(MembershipEntry localSiloEntry, MembershipTableData table)
        {
            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            foreach (var item in table.Members)
            {
                var entry = item.Item1;
                entries.Add(entry.SiloAddress, entry);
            }

            if (entries.TryGetValue(localSiloEntry.SiloAddress, out var existing))
            {
                entries[localSiloEntry.SiloAddress] = existing.WithStatus(localSiloEntry.Status);
            }
            else
            {
                entries[localSiloEntry.SiloAddress] = localSiloEntry;
            }

            var version = new MembershipVersion(table.Version.Version);
            return new MembershipTableSnapshot(localSiloEntry, version, entries.ToImmutable());
        }

        public MembershipEntry LocalSilo { get; }
        public MembershipVersion Version { get; }
        public ImmutableDictionary<SiloAddress, MembershipEntry> Entries { get; }

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

    }
}
