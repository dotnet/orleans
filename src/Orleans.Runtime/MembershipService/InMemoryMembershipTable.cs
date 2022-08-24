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
                new MembershipTableData(this.deepCopier.Copy(data), tableVersion)
                : new MembershipTableData(tableVersion);
        }

        public MembershipTableData ReadAll()
        {
            return new MembershipTableData(siloTable.Values.Select(tuple => 
                new Tuple<MembershipEntry, string>(this.deepCopier.Copy(tuple.Item1), tuple.Item2)).ToList(), tableVersion);
        }

        public TableVersion ReadTableVersion()
        {
            return tableVersion;
        }

        public bool Insert(MembershipEntry entry, TableVersion version)
        {
            Tuple<MembershipEntry, string> data;
            siloTable.TryGetValue(entry.SiloAddress, out data);
            if (data != null) return false;
            if (!tableVersion.VersionEtag.Equals(version.VersionEtag)) return false;
            
            siloTable[entry.SiloAddress] = new Tuple<MembershipEntry, string>(
                entry, lastETagCounter++.ToString(CultureInfo.InvariantCulture));
            tableVersion = new TableVersion(version.Version, NewETag());
            return true;
        }

        public bool Update(MembershipEntry entry, string etag, TableVersion version)
        {
            Tuple<MembershipEntry, string> data;
            siloTable.TryGetValue(entry.SiloAddress, out data);
            if (data == null) return false;
            if (!data.Item2.Equals(etag) || !tableVersion.VersionEtag.Equals(version.VersionEtag)) return false;
            
            siloTable[entry.SiloAddress] = new Tuple<MembershipEntry, string>(
                entry, lastETagCounter++.ToString(CultureInfo.InvariantCulture));
            tableVersion = new TableVersion(version.Version, NewETag());
            return true;
        }

        public void UpdateIAmAlive(MembershipEntry entry)
        {
            Tuple<MembershipEntry, string> data;
            siloTable.TryGetValue(entry.SiloAddress, out data);
            if (data == null) return;

            data.Item1.IAmAliveTime = entry.IAmAliveTime;
            siloTable[entry.SiloAddress] = new Tuple<MembershipEntry, string>(data.Item1, NewETag());
        }

        public override string ToString() => $"Table = {ReadAll()}, ETagCounter={lastETagCounter}";

        private string NewETag()
        {
            return lastETagCounter++.ToString(CultureInfo.InvariantCulture);
        }
    }
}
