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
using System.Linq;
using System.Reflection;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.MembershipService
{
    internal class MembershipOracleData
    {
        private readonly Dictionary<SiloAddress, MembershipEntry> localTable;  // all silos not including current silo
        private Dictionary<SiloAddress, SiloStatus> localTableCopy;            // a cached copy of a local table, including current silo, for fast access
        private Dictionary<SiloAddress, SiloStatus> localTableCopyOnlyActive;  // a cached copy of a local table, for fast access, including only active nodes and current silo (if active)
        private Dictionary<SiloAddress, string> localNamesTableCopy;           // a cached copy of a map from SiloAddress to Silo Name, not including current silo, for fast access

        private readonly List<ISiloStatusListener> statusListeners;
        private readonly TraceLogger logger;
        
        private IntValueStatistic clusterSizeStatistic;
        private StringValueStatistic clusterStatistic;

        internal readonly DateTime SiloStartTime;
        internal readonly SiloAddress MyAddress;
        internal readonly string MyHostname;
        internal SiloStatus CurrentStatus { get; private set; } // current status of this silo.
        internal string SiloName { get; private set; } // name of this silo.

        internal MembershipOracleData(Silo silo, TraceLogger log)
        {
            logger = log;
            localTable = new Dictionary<SiloAddress, MembershipEntry>();  
            localTableCopy = new Dictionary<SiloAddress, SiloStatus>();       
            localTableCopyOnlyActive = new Dictionary<SiloAddress, SiloStatus>();
            localNamesTableCopy = new Dictionary<SiloAddress, string>();  
            statusListeners = new List<ISiloStatusListener>();
            
            SiloStartTime = DateTime.UtcNow;
            MyAddress = silo.SiloAddress;
            MyHostname = silo.LocalConfig.DNSHostName;
            SiloName = silo.LocalConfig.SiloName;
            CurrentStatus = SiloStatus.Created;
            clusterSizeStatistic = IntValueStatistic.FindOrCreate(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER_SIZE, () => localTableCopyOnlyActive.Count);
            clusterStatistic = StringValueStatistic.FindOrCreate(StatisticNames.MEMBERSHIP_ACTIVE_CLUSTER,
                    () => 
                        {
                            List<string> list = localTableCopyOnlyActive.Keys.Select(addr => addr.ToLongString()).ToList();
                            list.Sort();
                            return Utils.EnumerableToString(list);
                        });
        }

        
        // ONLY access localTableCopy and not the localTable, to prevent races, as this method may be called outside the turn.
        internal SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            var status = SiloStatus.None;
            if (siloAddress.Equals(MyAddress))
            {
                status = CurrentStatus;
            }
            else
            {
                if (!localTableCopy.TryGetValue(siloAddress, out status))
                {
                    if (CurrentStatus.Equals(SiloStatus.Active))
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.Runtime_Error_100209, "-The given siloAddress {0} is not registered in this MembershipOracle.", siloAddress.ToLongString());
                    status = SiloStatus.None;
                }
            }
            if (logger.IsVerbose3) logger.Verbose3("-GetApproximateSiloStatus returned {0} for silo: {1}", status, siloAddress.ToLongString());
            return status;
        }

        // ONLY access localTableCopy or localTableCopyOnlyActive and not the localTable, to prevent races, as this method may be called outside the turn.
        internal Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            Dictionary<SiloAddress, SiloStatus> dict = onlyActive ? localTableCopyOnlyActive : localTableCopy;
            if (logger.IsVerbose3) logger.Verbose3("-GetApproximateSiloStatuses returned {0} silos: {1}", dict.Count, Utils.DictionaryToString(dict));
            return dict;
        }

        internal bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            if (siloAddress.Equals(MyAddress))
            {
                siloName = SiloName;
                return true;
            }
            return localNamesTableCopy.TryGetValue(siloAddress, out siloName);
        }

        internal bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            lock (statusListeners)
            {
                if (statusListeners.Contains(observer))
                    return false;
                
                statusListeners.Add(observer);
                return true;
            }
        }

        internal bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            lock (statusListeners)
            {
                return statusListeners.Contains(observer) && statusListeners.Remove(observer);
            }
        }

        internal void UpdateMyStatusLocal(SiloStatus status)
        {
            if (CurrentStatus == status) return;

            // make copies
            var tmpLocalTableCopy = GetSiloStatuses(st => true, true); // all the silos including me.
            var tmpLocalTableCopyOnlyActive = GetSiloStatuses(st => st.Equals(SiloStatus.Active), true);    // only active silos including me.
            var tmpLocalTableNamesCopy = localTable.ToDictionary(pair => pair.Key, pair => pair.Value.InstanceName);   // all the silos excluding me.

            CurrentStatus = status;

            tmpLocalTableCopy[MyAddress] = status;

            if (status.Equals(SiloStatus.Active))
            {
                tmpLocalTableCopyOnlyActive[MyAddress] = status;
            }
            else if (tmpLocalTableCopyOnlyActive.ContainsKey(MyAddress))
            {
                tmpLocalTableCopyOnlyActive.Remove(MyAddress);
            }
            localTableCopy = tmpLocalTableCopy;
            localTableCopyOnlyActive = tmpLocalTableCopyOnlyActive;
            localNamesTableCopy = tmpLocalTableNamesCopy;
            NotifyLocalSubscribers(MyAddress, CurrentStatus);
        }

        private SiloStatus GetSiloStatus(SiloAddress siloAddress)
        {
            if (siloAddress.Equals(MyAddress))
                return CurrentStatus;
            
            MembershipEntry data;
            return !localTable.TryGetValue(siloAddress, out data) ? SiloStatus.None : data.Status;
        }

        internal MembershipEntry GetSiloEntry(SiloAddress siloAddress)
        {
            return localTable[siloAddress];
        }

        internal Dictionary<SiloAddress, SiloStatus> GetSiloStatuses(Func<SiloStatus, bool> filter, bool includeMyself)
        {
            Dictionary<SiloAddress, SiloStatus> dict = localTable.Where(
                pair => filter(pair.Value.Status)).ToDictionary(pair => pair.Key, pair => pair.Value.Status);

            if (includeMyself && filter(CurrentStatus)) // add myself
                dict.Add(MyAddress, CurrentStatus);
            
            return dict;
        }

        internal MembershipEntry CreateNewMembershipEntry(NodeConfiguration nodeConf, SiloStatus myStatus)
        {
            return CreateNewMembershipEntry(nodeConf, MyAddress, MyHostname, myStatus, SiloStartTime);
        }

        private static MembershipEntry CreateNewMembershipEntry(NodeConfiguration nodeConf, SiloAddress myAddress, string myHostname, SiloStatus myStatus, DateTime startTime)
        {
            var assy = Assembly.GetEntryAssembly() ?? typeof(MembershipOracleData).GetTypeInfo().Assembly;
            var roleName = assy.GetName().Name;

            var entry = new MembershipEntry
            {
                SiloAddress = myAddress,

                HostName = myHostname,
                InstanceName = nodeConf.SiloName,

                Status = myStatus,
                ProxyPort = (nodeConf.IsGatewayNode ? nodeConf.ProxyGatewayEndpoint.Port : 0),

                RoleName = roleName,
                
                SuspectTimes = new List<Tuple<SiloAddress, DateTime>>(),
                StartTime = startTime,
                IAmAliveTime = DateTime.UtcNow
            };
            return entry;
        }

        internal bool TryUpdateStatusAndNotify(MembershipEntry entry)
        {
            if (!TryUpdateStatus(entry)) return false;

            localTableCopy = GetSiloStatuses(status => true, true); // all the silos including me.
            localTableCopyOnlyActive = GetSiloStatuses(status => status.Equals(SiloStatus.Active), true);    // only active silos including me.
            localNamesTableCopy = localTable.ToDictionary(pair => pair.Key, pair => pair.Value.InstanceName);   // all the silos excluding me.

            if (logger.IsVerbose) logger.Verbose("-Updated my local view of {0} status. It is now {1}.", entry.SiloAddress.ToLongString(), GetSiloStatus(entry.SiloAddress));

            NotifyLocalSubscribers(entry.SiloAddress, entry.Status);
            return true;
        }

        // return true if the status changed
        private bool TryUpdateStatus(MembershipEntry updatedSilo)
        {
            MembershipEntry currSiloData = null;
            if (!localTable.TryGetValue(updatedSilo.SiloAddress, out currSiloData))
            {
                // an optimization - if I learn about dead silo and I never knew about him before, I don't care, can just ignore him.
                if (updatedSilo.Status == SiloStatus.Dead) return false;

                localTable.Add(updatedSilo.SiloAddress, updatedSilo);
                return true;
            }

            if (currSiloData.Status == updatedSilo.Status) return false;

            currSiloData.Update(updatedSilo);
            return true;
        }

        private void NotifyLocalSubscribers(SiloAddress siloAddress, SiloStatus newStatus)
        {
            if (logger.IsVerbose2) logger.Verbose2("-NotifyLocalSubscribers about {0} status {1}", siloAddress.ToLongString(), newStatus);
            
            List<ISiloStatusListener> copy;
            lock (statusListeners)
            {
                copy = statusListeners.ToList();
            }

            foreach (ISiloStatusListener listener in copy)
            {
                try
                {
                    listener.SiloStatusChangeNotification(siloAddress, newStatus);
                }
                catch (Exception exc)
                {
                    logger.Error(ErrorCode.MembershipLocalSubscriberException,
                        String.Format("Local ISiloStatusListener {0} has thrown an exception when was notified about SiloStatusChangeNotification about silo {1} new status {2}",
                        listener.GetType().FullName, siloAddress.ToLongString(), newStatus), exc);
                }
            }
        }

        public override string ToString()
        {
            return string.Format("CurrentSiloStatus = {0}, {1} silos: {2}.",
                CurrentStatus,
                localTableCopy.Count,
                Utils.EnumerableToString(localTableCopy, pair => 
                    String.Format("SiloAddress={0} Status={1}", pair.Key.ToLongString(), pair.Value)));
        }
    }
}
