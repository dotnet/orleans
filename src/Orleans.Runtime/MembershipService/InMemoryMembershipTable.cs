using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Serialization;

namespace Orleans.Runtime.MembershipService
{
    internal class InMemoryMembershipTable
    {
        private readonly Dictionary<SiloAddress, Tuple<MembershipEntry, string>> siloTable;
        private TableVersion tableVersion;
        private long lastETagCounter;

        [NonSerialized]
        private readonly DeepCopier deepCopier;

        public InMemoryMembershipTable(DeepCopier deepCopier)
        {
            this.deepCopier = deepCopier;
            siloTable = new Dictionary<SiloAddress, Tuple<MembershipEntry, string>>();
            lastETagCounter = 0;
            tableVersion = new TableVersion(0, NewETag());
        }

        public MembershipTableData Read(SiloAddress key)
        {
            return siloTable.TryGetValue(key, out var data) ?
                // The copier preserves the null state of its input.
                new MembershipTableData(this.deepCopier.Copy(data)!, tableVersion)
                : new MembershipTableData(tableVersion);
        }

        public MembershipTableData ReadAll()
        {
            return new MembershipTableData(siloTable.Values.Select(tuple =>
                // The copier preserves the null state of its input.
                new Tuple<MembershipEntry, string>(this.deepCopier.Copy(tuple.Item1)!, tuple.Item2)).ToList(), tableVersion);
        }

        public TableVersion ReadTableVersion()
        {
            return tableVersion;
        }

        public bool Insert(MembershipEntry entry, TableVersion version) => InsertWithResult(entry, version).Succeeded;

        public MembershipTableWriteResult InsertWithResult(MembershipEntry entry, TableVersion version)
        {
            siloTable.TryGetValue(entry.SiloAddress, out var data);
            if (data != null) return default;
            if (!tableVersion.VersionEtag.Equals(version.VersionEtag, StringComparison.Ordinal)) return default;

            var rowETag = NewETag();
            siloTable[entry.SiloAddress] = new Tuple<MembershipEntry, string>(
                entry.Copy(), rowETag);
            tableVersion = new TableVersion(version.Version, NewETag());
            return new(true, new(tableVersion, rowETag));
        }

        public bool Update(MembershipEntry entry, string etag, TableVersion version) => UpdateWithResult(entry, etag, version).Succeeded;

        public MembershipTableWriteResult UpdateWithResult(MembershipEntry entry, string etag, TableVersion version)
        {
            siloTable.TryGetValue(entry.SiloAddress, out var data);
            if (data == null) return default;
            if (!data.Item2.Equals(etag, StringComparison.Ordinal) || !tableVersion.VersionEtag.Equals(version.VersionEtag, StringComparison.Ordinal)) return default;

            var rowETag = NewETag();
            siloTable[entry.SiloAddress] = new Tuple<MembershipEntry, string>(
                entry.Copy(), rowETag);
            tableVersion = new TableVersion(version.Version, NewETag());
            return new(true, new(tableVersion, rowETag));
        }

        public void UpdateIAmAlive(MembershipEntry entry)
        {
            siloTable.TryGetValue(entry.SiloAddress, out var data);
            if (data == null) return;

            data.Item1.IAmAliveTime = entry.IAmAliveTime;
        }

        public override string ToString() => $"Table = {ReadAll()}, ETagCounter={lastETagCounter}";

        private string NewETag()
        {
            return lastETagCounter++.ToString(CultureInfo.InvariantCulture);
        }

        public void CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            var removedEntries = new List<SiloAddress>();
            foreach (var (key, (value, _)) in siloTable)
            {
                if (value.Status == SiloStatus.Dead
                    && value.EffectiveUpdateTime < beforeDate)
                {
                    removedEntries.Add(key);
                }
            }

            foreach (var removedEntry in removedEntries)
            {
                siloTable.Remove(removedEntry);
            }
        }
    }
}
