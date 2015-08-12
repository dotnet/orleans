/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orleans.Runtime.MembershipService
{
    [Serializable]
    internal class InMemoryMembershipTable
    {
        private readonly Dictionary<SiloAddress, Tuple<MembershipEntry, string>> siloTable;
        private TableVersion tableVersion;
        private long lastETagCounter;

        public InMemoryMembershipTable()
        {
            siloTable = new Dictionary<SiloAddress, Tuple<MembershipEntry, string>>();
            lastETagCounter = 0;
            tableVersion = new TableVersion(0, NewETag());
        }

        public MembershipTableData Read(SiloAddress key)
        {
            return siloTable.ContainsKey(key) ? 
                new MembershipTableData(siloTable[key], tableVersion) : new MembershipTableData(tableVersion);
        }

        public MembershipTableData ReadAll()
        {
            return new MembershipTableData(siloTable.Values.Select(tuple => 
                new Tuple<MembershipEntry, string>(tuple.Item1, tuple.Item2)).ToList(), tableVersion);
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

        public override string ToString()
        {
            return String.Format("Table = {0}, ETagCounter={1}", ReadAll().ToString(), lastETagCounter);
        }

        private string NewETag()
        {
            return lastETagCounter++.ToString(CultureInfo.InvariantCulture);
        }
    }
}
