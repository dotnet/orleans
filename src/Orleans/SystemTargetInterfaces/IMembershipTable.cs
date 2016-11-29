using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    /// <summary>
    /// Interface for Membership Table.
    /// </summary>
    public interface IMembershipTable
    {
        /// <summary>
        /// Initializes the membership table, will be called before all other methods
        /// </summary>
        /// <param name="globalConfiguration">the give global configuration</param>
        /// <param name="tryInitTableVersion">whether an attempt will be made to init the underlying table</param>
        /// <param name="logger">the logger used by the membership table</param>
        Task InitializeMembershipTable(GlobalConfiguration globalConfiguration, bool tryInitTableVersion, Logger logger);

        /// <summary>
        /// Deletes all table entries of the given deploymentId
        /// </summary>
        Task DeleteMembershipTableEntries(string deploymentId);

        /// <summary>
        /// Atomically reads the Membership Table information about a given silo.
        /// The returned MembershipTableData includes one MembershipEntry entry for a given silo and the 
        /// TableVersion for this table. The MembershipEntry and the TableVersion have to be read atomically.
        /// </summary>
        /// <param name="key">The address of the silo whose membership information needs to be read.</param>
        /// <returns>The membership information for a given silo: MembershipTableData consisting one MembershipEntry entry and
        /// TableVersion, read atomically.</returns>
        Task<MembershipTableData> ReadRow(SiloAddress key);

        /// <summary>
        /// Atomically reads the full content of the Membership Table.
        /// The returned MembershipTableData includes all MembershipEntry entry for all silos in the table and the 
        /// TableVersion for this table. The MembershipEntries and the TableVersion have to be read atomically.
        /// </summary>
        /// <returns>The membership information for a given table: MembershipTableData consisting multiple MembershipEntry entries and
        /// TableVersion, all read atomically.</returns>
        Task<MembershipTableData> ReadAll();

        /// <summary>
        /// Atomically tries to insert (add) a new MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) New MembershipEntry will be added to the table.
        /// 2) The newly added MembershipEntry will also be added with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, insert of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo already exist in the table
        /// 2) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be inserted.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the insert operation succeeded and false otherwise.</returns>
        Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion);

        /// <summary>
        /// Atomically tries to update the MembershipEntry for one silo and also update the TableVersion.
        /// If operation succeeds, the following changes would be made to the table:
        /// 1) The MembershipEntry for this silo will be updated to the new MembershipEntry (the old entry will be fully substitued by the new entry) 
        /// 2) The eTag for the updated MembershipEntry will also be eTag with the new unique automatically generated eTag.
        /// 3) TableVersion.Version in the table will be updated to the new TableVersion.Version.
        /// 4) TableVersion etag in the table will be updated to the new unique automatically generated eTag.
        /// All those changes to the table, update of a new row and update of the table version and the associated etags, should happen atomically, or fail atomically with no side effects.
        /// The operation should fail in each of the following conditions:
        /// 1) A MembershipEntry for a given silo does not exist in the table
        /// 2) A MembershipEntry for a given silo exist in the table but its etag in the table does not match the provided etag.
        /// 3) Update of the TableVersion failed since the given TableVersion etag (as specified by the TableVersion.VersionEtag property) did not match the TableVersion etag in the table.
        /// </summary>
        /// <param name="entry">MembershipEntry to be updated.</param>
        /// <param name="etag">The etag  for the given MembershipEntry.</param>
        /// <param name="tableVersion">The new TableVersion for this table, along with its etag.</param>
        /// <returns>True if the update operation succeeded and false otherwise.</returns>
        Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion);

        /// <summary>
        /// Updates the IAmAlive part (column) of the MembershipEntry for this silo.
        /// This operation should only update the IAmAlive collumn and not change other columns.
        /// This operation is a "dirty write" or "in place update" and is performed without etag validation. 
        /// With regards to eTags update:
        /// This operation may automatically update the eTag associated with the given silo row, but it does not have to. It can also leave the etag not changed ("dirty write").
        /// With regards to TableVersion:
        /// this operation should not change the TableVersion of the table. It should leave it untouched.
        /// There is no scenario where this operation could fail due to table semantical reasons. It can only fail due to network problems or table unavailability.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns>Task representing the successful execution of this operation. </returns>
        Task UpdateIAmAlive(MembershipEntry entry);
    }

    /// <summary>
    /// Membership table interface for grain based implementation.
    /// </summary>
    [Unordered]
    public interface IMembershipTableGrain : IGrainWithGuidKey, IMembershipTable
    {
        
    }

    [Serializable]
    [Immutable]
    public class TableVersion
    {
        /// <summary>
        /// The version part of this TableVersion. Monotonically increasing number.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// The etag of this TableVersion, used for validation of table update operations.
        /// </summary>
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
    public class MembershipTableData
    {
        public IReadOnlyList<Tuple<MembershipEntry, string>> Members { get; private set; }

        public TableVersion Version { get; private set; }

        public MembershipTableData(List<Tuple<MembershipEntry, string>> list, TableVersion version)
        {
            // put deads at the end, just for logging.
            list.Sort(
               (x, y) =>
               {
                   if (x.Item1.Status == SiloStatus.Dead) return 1; // put Deads at the end
                   if (y.Item1.Status == SiloStatus.Dead) return -1; // put Deads at the end
                   return String.Compare(x.Item1.SiloName, y.Item1.SiloName, StringComparison.Ordinal);
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
                Utils.EnumerableToString(Members, tuple => tuple.Item1.ToFullString()));
        }

        // return a copy of the table removing all dead appereances of dead nodes, except for the last one.
        public MembershipTableData SupressDuplicateDeads()
        {
            var dead = new Dictionary<IPEndPoint, Tuple<MembershipEntry, string>>();
            // pick only latest Dead for each instance
            foreach (var next in Members.Where(item => item.Item1.Status == SiloStatus.Dead))
            {
                var ipEndPoint = next.Item1.SiloAddress.Endpoint;
                Tuple<MembershipEntry, string> prev;
                if (!dead.TryGetValue(ipEndPoint, out prev))
                {
                    dead[ipEndPoint] = next;
                }
                else
                {
                    // later dead.
                    if (next.Item1.SiloAddress.Generation.CompareTo(prev.Item1.SiloAddress.Generation) > 0)
                        dead[ipEndPoint] = next;
                }
            }
            //now add back non-dead
            List<Tuple<MembershipEntry, string>> all = dead.Values.ToList();
            all.AddRange(Members.Where(item => item.Item1.Status != SiloStatus.Dead));
            return new MembershipTableData(all, Version);
        }
    }

    [Serializable]
    public class MembershipEntry
    {
        /// <summary>
        /// The silo unique identity (ip:port:epoch). Used mainly by the Membership Protocol.
        /// </summary>
        public SiloAddress SiloAddress { get; set; }

        /// <summary>
        /// The silo status. Managed by the Membership Protocol.
        /// </summary>
        public SiloStatus Status { get; set; }

        /// <summary>
        /// The list of silos that suspect this silo. Managed by the Membership Protocol.
        /// </summary>
        public List<Tuple<SiloAddress, DateTime>> SuspectTimes { get; set; }

        /// <summary>
        /// Silo to clients TCP port. Set on silo startup.
        /// </summary>    
        public int ProxyPort { get; set; }

        /// <summary>
        /// The DNS host name of the silo. Equals to Dns.GetHostName(). Set on silo startup.
        /// </summary>
        public string HostName { get; set; }

        /// <summary>
        /// the name of the specific silo instance within a cluster. 
        /// If running in Azure - the name of this role instance. Set on silo startup.
        /// </summary>
        public string SiloName { get; set; }

        public string RoleName { get; set; } // Optional - only for Azure role  
        public int UpdateZone { get; set; }  // Optional - only for Azure role
        public int FaultZone { get; set; }   // Optional - only for Azure role

        /// <summary>
        /// Time this silo was started. For diagnostics and troubleshooting only.
        /// </summary>
        public DateTime StartTime { get; set; }

        /// <summary>
        /// the last time this silo reported that it is alive. For diagnostics and troubleshooting only.
        /// </summary>
        public DateTime IAmAliveTime { get; set; }
        

        private static readonly List<Tuple<SiloAddress, DateTime>> EmptyList = new List<Tuple<SiloAddress, DateTime>>(0);

        public void AddSuspector(SiloAddress suspectingSilo, DateTime suspectingTime)
        {
            if (SuspectTimes == null)
                SuspectTimes = new List<Tuple<SiloAddress, DateTime>>();

            var suspector = new Tuple<SiloAddress, DateTime>(suspectingSilo, suspectingTime);
            SuspectTimes.Add(suspector);
        }

        // partialUpdate arrivies via gossiping with other oracles. In such a case only take the status.
        internal void Update(MembershipEntry updatedSiloEntry)
        {
            SiloAddress = updatedSiloEntry.SiloAddress;
            Status = updatedSiloEntry.Status;
            //---
            HostName = updatedSiloEntry.HostName;
            ProxyPort = updatedSiloEntry.ProxyPort;

            RoleName = updatedSiloEntry.RoleName;
            SiloName = updatedSiloEntry.SiloName;
            UpdateZone = updatedSiloEntry.UpdateZone;
            FaultZone = updatedSiloEntry.FaultZone;

            SuspectTimes = updatedSiloEntry.SuspectTimes;
            StartTime = updatedSiloEntry.StartTime;
            IAmAliveTime = updatedSiloEntry.IAmAliveTime;
        }

        internal List<Tuple<SiloAddress, DateTime>> GetFreshVotes(TimeSpan expiration)
        {
            if (SuspectTimes == null)
                return EmptyList;
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

        internal void TryUpdateStartTime(DateTime startTime)
        {
            if (StartTime.Equals(default(DateTime)))
                StartTime = startTime;
        }

        public override string ToString()
        {
            return string.Format("SiloAddress={0} SiloName={1} Status={2}", SiloAddress.ToLongString(), SiloName, Status);
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
            return string.Format("[SiloAddress={0} SiloName={1} Status={2} HostName={3} ProxyPort={4} " +
                                 "RoleName={5} UpdateZone={6} FaultZone={7} StartTime = {8} IAmAliveTime = {9} {10} {11}]",
                SiloAddress.ToLongString(),
                SiloName,
                Status,
                HostName,
                ProxyPort,
                RoleName,
                UpdateZone,
                FaultZone,
                LogFormatter.PrintDate(StartTime),
                LogFormatter.PrintDate(IAmAliveTime),
                suspecters == null
                    ? ""
                    : "Suspecters = " + Utils.EnumerableToString(suspecters, sa => sa.ToLongString()),
                timestamps == null
                    ? ""
                    : "SuspectTimes = " + Utils.EnumerableToString(timestamps, LogFormatter.PrintDate)
                );
        }
    }
}
