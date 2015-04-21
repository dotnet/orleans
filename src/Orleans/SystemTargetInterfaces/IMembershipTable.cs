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

﻿using System;
using System.Collections.Generic;
﻿using System.Collections.ObjectModel;
﻿using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Concurrency;

namespace Orleans
{
    /// <summary>
    /// Development mode grain-based implementation of membership table
    /// </summary>
    [Unordered]
    internal interface IMembershipTable : IGrain
    {
        Task<MembershipTableData> ReadRow(SiloAddress key);

        Task<MembershipTableData> ReadAll();

        Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion);

        /// <summary>
        /// Writes a new entry iff the entry etag is equal to the provided etag parameter.
        /// </summary>
        /// <param name="entry"></param>
        /// <param name="etag"></param>
        /// <param name="tableVersion"></param>
        /// <returns>true iff the write was successful</returns>
        Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion);

        /// <summary>
        /// Update the IAmAlive timestamp for this silo.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        Task UpdateIAmAlive(MembershipEntry entry);
    }
    
    [Serializable]
    [Immutable]
    internal class TableVersion
    {
        public int Version { get; private set; }
        public string VersionEtag { get; private set; }

        public TableVersion(int version, string eTag)
        {
            Version = version;
            VersionEtag = eTag;
        }

        public TableVersion Next()
        {
            return new TableVersion(Version + 1, VersionEtag);
        }

        public override string ToString()
        {
            return string.Format("<{0}, {1}>", Version, VersionEtag);
        }
    }

    [Serializable]
    internal class MembershipTableData
    {
        public IReadOnlyList<Tuple<MembershipEntry, string>> Members { get; private set; }

        public TableVersion Version { get; private set; }

        public MembershipTableData(List<Tuple<MembershipEntry, string>> list, TableVersion version)
        {
            // put deads at the end, just for logging.
            list.Sort(
               (x, y) =>
               {
                   if (x.Item1.Status.Equals(SiloStatus.Dead)) return 1; // put Deads at the end
                   return 1;
               });
            Members = list.AsReadOnly();
            Version = version;
        }

        public MembershipTableData(Tuple<MembershipEntry, string> tuple, TableVersion version)
        {
            Members = (new List<Tuple<MembershipEntry, string>> { tuple }).AsReadOnly();
            Version = version;
        }

        public MembershipTableData(TableVersion version)
        {
            Members = (new List<Tuple<MembershipEntry, string>>()).AsReadOnly();
            Version = version;
        }

        public Tuple<MembershipEntry, string> Get(SiloAddress silo)
        {
            return Members.First(tuple => tuple.Item1.SiloAddress.Equals(silo));
        }

        public bool Contains(SiloAddress silo)
        {
            return Members.Any(tuple => tuple.Item1.SiloAddress.Equals(silo));
        }

        public override string ToString()
        {
            int active = Members.Count(e => e.Item1.Status == SiloStatus.Active);
            int dead = Members.Count(e => e.Item1.Status == SiloStatus.Dead);
            int created = Members.Count(e => e.Item1.Status == SiloStatus.Created);
            int joining = Members.Count(e => e.Item1.Status == SiloStatus.Joining);
            int shuttingDown = Members.Count(e => e.Item1.Status == SiloStatus.ShuttingDown);
            int stopping = Members.Count(e => e.Item1.Status == SiloStatus.Stopping);

            string otherCounts = String.Format("{0}{1}{2}{3}",
                                created > 0 ? (", " + created + " are Created") : "",
                                joining > 0 ? (", " + joining + " are Joining") : "",
                                shuttingDown > 0 ? (", " + shuttingDown + " are ShuttingDown") : "",
                                stopping > 0 ? (", " + stopping + " are Stopping") : "");

            return string.Format("{0} silos, {1} are Active, {2} are Dead{3}, Version={4}. All silos: {5}",
                Members.Count,
                active,
                dead,
                otherCounts,
                Version,
                Utils.EnumerableToString(Members, (tuple) => tuple.Item1.ToFullString()));
        }

        // return a copy of the table removing all dead appereances of dead nodes, except for the last one.
        public MembershipTableData SupressDuplicateDeads()
        {
            var dead = new Dictionary<string, Tuple<MembershipEntry, string>>();
            // pick only latest Dead for each instance
            foreach (var next in this.Members.Where(item => item.Item1.Status == SiloStatus.Dead))
            {
                var name = next.Item1.InstanceName;
                Tuple<MembershipEntry, string> prev = null;
                if (!dead.TryGetValue(name, out prev))
                {
                    dead[name] = next;
                }
                else
                {
                    // later dead.
                    if (next.Item1.SiloAddress.Generation.CompareTo(prev.Item1.SiloAddress.Generation) > 0)
                        dead[name] = next;
                }
            }
            //now add back non-dead
            List<Tuple<MembershipEntry, string>> all = dead.Values.ToList();
            all.AddRange(this.Members.Where(item => item.Item1.Status != SiloStatus.Dead));
            return new MembershipTableData(all, this.Version);
        }
    }

    [Serializable]
    internal class MembershipEntry
    {
        public SiloAddress SiloAddress { get; set; }

        public string HostName { get; set; }          
        public SiloStatus Status { get; set; }          
        public int ProxyPort { get; set; }             
        public bool IsPrimary { get; set; }           

        public string RoleName { get; set; }              // Optional - only for Azure role
        public string InstanceName { get; set; }          // Optional - only for Azure role
        public int UpdateZone { get; set; }            // Optional - only for Azure role
        public int FaultZone { get; set; }             // Optional - only for Azure role

        public DateTime StartTime { get; set; }             // Time this silo was started. For diagnostics.
        public DateTime IAmAliveTime { get; set; }          // Time this silo updated it was alive. For diagnostics.

        public List<Tuple<SiloAddress, DateTime>> SuspectTimes { get; set; }

        private static readonly List<Tuple<SiloAddress, DateTime>> emptyList = new List<Tuple<SiloAddress, DateTime>>(0);

        // partialUpdate arrivies via gossiping with other oracles. In such a case only take the status.
        internal void Update(MembershipEntry updatedSiloEntry)
        {
            SiloAddress = updatedSiloEntry.SiloAddress;
            Status = updatedSiloEntry.Status;
            //---
            HostName = updatedSiloEntry.HostName;
            ProxyPort = updatedSiloEntry.ProxyPort;
            IsPrimary = updatedSiloEntry.IsPrimary;

            RoleName = updatedSiloEntry.RoleName;
            InstanceName = updatedSiloEntry.InstanceName;
            UpdateZone = updatedSiloEntry.UpdateZone;
            FaultZone = updatedSiloEntry.FaultZone;

            SuspectTimes = updatedSiloEntry.SuspectTimes;
            StartTime = updatedSiloEntry.StartTime;
            IAmAliveTime = updatedSiloEntry.IAmAliveTime;
        }

        internal List<Tuple<SiloAddress, DateTime>> GetFreshVotes(TimeSpan expiration)
        {
            if (SuspectTimes == null)
                return emptyList;
            DateTime now = DateTime.UtcNow;
            return SuspectTimes.FindAll(voter =>
                {
                    DateTime otherVoterTime = voter.Item2;
                    // If now is smaller than otherVoterTime, than assume the otherVoterTime is fresh.
                    // This could happen if clocks are not synchronized and the other voter clock is ahead of mine.
                    if (now < otherVoterTime) 
                        return true;

                    return now.Subtract(otherVoterTime) < expiration;
                });
        }

        internal void AddSuspector(SiloAddress suspectingSilo, DateTime suspectingTime)
        {
            if (SuspectTimes == null)
                SuspectTimes = new List<Tuple<SiloAddress, DateTime>>();

            var suspector = new Tuple<SiloAddress, DateTime>(suspectingSilo, suspectingTime);
            SuspectTimes.Add(suspector);
        }

        internal void TryUpdateStartTime(DateTime startTime)
        {
            if (StartTime.Equals(default(DateTime)))
                StartTime = startTime;
        }

        public override string ToString()
        {
            return string.Format("SiloAddress={0} Status={1}", SiloAddress.ToLongString(), Status);
        }

        internal string ToFullString(bool full = false)
        {
            if (!full)
                return ToString();

            List<SiloAddress> suspecters = SuspectTimes == null
                ? null
                : SuspectTimes.Select(tuple => tuple.Item1).ToList();
            List<DateTime> timestamps = SuspectTimes == null
                ? null
                : SuspectTimes.Select(tuple => tuple.Item2).ToList();
            return string.Format("[SiloAddress={0} Status={1} HostName={2} ProxyPort={3} IsPrimary={4} " +
                                 "RoleName={5} InstanceName={6} UpdateZone={7} FaultZone={8} StartTime = {9} IAmAliveTime = {10} {11} {12}]",
                SiloAddress.ToLongString(),
                Status,
                HostName,
                ProxyPort,
                IsPrimary,
                RoleName,
                InstanceName,
                UpdateZone,
                FaultZone,
                TraceLogger.PrintDate(StartTime),
                TraceLogger.PrintDate(IAmAliveTime),
                suspecters == null
                    ? ""
                    : "Suspecters = " + Utils.EnumerableToString(suspecters, (SiloAddress sa) => sa.ToLongString()),
                timestamps == null
                    ? ""
                    : "SuspectTimes = " + Utils.EnumerableToString(timestamps, TraceLogger.PrintDate)
                );
        }
    }
}