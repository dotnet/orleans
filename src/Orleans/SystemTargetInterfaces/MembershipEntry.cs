using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Runtime;

namespace Orleans
{
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