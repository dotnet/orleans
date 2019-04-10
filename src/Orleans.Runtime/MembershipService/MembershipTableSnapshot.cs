using System.Collections.Immutable;

namespace Orleans.Runtime.MembershipService
{
    internal sealed class MembershipTableSnapshot
    {
        public MembershipTableSnapshot(
            ILocalSiloDetails localSilo,
            MembershipVersion version,
            ImmutableDictionary<SiloAddress, MembershipEntry> entries)
        {
            this.LocalSilo = localSilo;
            this.Version = version;
            this.Entries = entries;

            var statuses = ImmutableDictionary.CreateBuilder<SiloAddress, SiloStatus>();
            var activeStatuses = ImmutableDictionary.CreateBuilder<SiloAddress, SiloStatus>();
            var names = ImmutableDictionary.CreateBuilder<SiloAddress, string>();
            foreach (var item in entries)
            {
                var entry = item.Value;
                statuses.Add(item.Key, entry.Status);
                if (entry.Status == SiloStatus.Active)
                {
                    activeStatuses.Add(item.Key, entry.Status);
                }

                names.Add(item.Key, entry.SiloName);
            }

            this.localTableCopy = statuses.ToImmutable();
            this.localTableCopyOnlyActive = activeStatuses.ToImmutable();
            this.localNamesTableCopy = names.ToImmutable();
        }

        public static MembershipTableSnapshot Create(ILocalSiloDetails localSilo, MembershipTableData table)
        {
            var entries = ImmutableDictionary.CreateBuilder<SiloAddress, MembershipEntry>();
            foreach (var item in table.Members)
            {
                var entry = item.Item1;
                entries.Add(entry.SiloAddress, entry);
            }

            var version = new MembershipVersion(table.Version.Version);
            return new MembershipTableSnapshot(localSilo, version, entries.ToImmutable());
        }

        public ILocalSiloDetails LocalSilo { get; }

        public MembershipVersion Version { get; }

        public ImmutableDictionary<SiloAddress, MembershipEntry> Entries { get; }

        /// <summary>
        /// A cached copy of a local table, including current silo, for fast access.
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloStatus> localTableCopy { get; }

        /// <summary>
        /// A cached copy of a local table, for fast access, including only active nodes and current silo (if active).
        /// </summary>
        public ImmutableDictionary<SiloAddress, SiloStatus> localTableCopyOnlyActive { get; }

        /// <summary>
        /// A copy of a map from SiloAddress to Silo Name for fast access.
        /// </summary>
        public ImmutableDictionary<SiloAddress, string> localNamesTableCopy { get; }
    }
}
