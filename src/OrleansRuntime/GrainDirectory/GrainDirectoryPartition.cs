using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Orleans.GrainDirectory;


namespace Orleans.Runtime.GrainDirectory
{
    [Serializable]
    internal class ActivationInfo : IActivationInfo
    {
        public SiloAddress SiloAddress { get; private set; }
        public DateTime TimeCreated { get; private set; }
        public GrainDirectoryEntryStatus RegistrationStatus { get; set; }

        public ActivationInfo(SiloAddress siloAddress, GrainDirectoryEntryStatus registrationStatus)
        {
            SiloAddress = siloAddress;
            TimeCreated = DateTime.UtcNow;
            RegistrationStatus = registrationStatus;
        }

        public ActivationInfo(IActivationInfo iActivationInfo)
        {
            SiloAddress = iActivationInfo.SiloAddress;
            TimeCreated = iActivationInfo.TimeCreated;
            RegistrationStatus = iActivationInfo.RegistrationStatus;
        }


        public bool OkToRemove(UnregistrationCause cause)
        {
            switch (cause)
            {
                case UnregistrationCause.Force:
                    return true;

                case UnregistrationCause.CacheInvalidation:
                    return RegistrationStatus == GrainDirectoryEntryStatus.Cached;

                case UnregistrationCause.NonexistentActivation:
                    {
                        if (RegistrationStatus == GrainDirectoryEntryStatus.Cached)
                            return true; // cache entries are always removed

                        var delayparameter = Silo.CurrentSilo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay;
                        if (delayparameter <= TimeSpan.Zero)
                            return false; // no lazy deregistration
                        else
                            return (TimeCreated <= DateTime.UtcNow - delayparameter);
                    }

                default:
                    throw new OrleansException("unhandled case");
            }
        }

        public override string ToString()
        {
            return String.Format("{0}, {1}", SiloAddress, TimeCreated);
        }
    }

    [Serializable]
    internal class GrainInfo : IGrainInfo
    {
        public Dictionary<ActivationId, IActivationInfo> Instances { get; private set; }
        public int VersionTag { get; private set; }
        public bool SingleInstance { get; private set; }

        private static readonly SafeRandom rand;
        internal const int NO_ETAG = -1;

        static GrainInfo()
        {
            rand = new SafeRandom();
        }

        internal GrainInfo()
        {
            Instances = new Dictionary<ActivationId, IActivationInfo>();
            VersionTag = 0;
            SingleInstance = false;
        }

        public bool AddActivation(ActivationId act, SiloAddress silo)
        {
            if (SingleInstance && (Instances.Count > 0) && !Instances.ContainsKey(act))
            {
                throw new InvalidOperationException(
                    "Attempting to add a second activation to an existing grain in single activation mode");
            }
            IActivationInfo info;
            if (Instances.TryGetValue(act, out info))
            {
                if (info.SiloAddress.Equals(silo))
                {
                    // just refresh, no need to generate new VersionTag
                    return false;
                }
            }
            Instances[act] = new ActivationInfo(silo, GrainDirectoryEntryStatus.ClusterLocal);
            VersionTag = rand.Next();
            return true;
        }

        public ActivationAddress AddSingleActivation(GrainId grain, ActivationId act, SiloAddress silo, GrainDirectoryEntryStatus registrationStatus)
        {
            SingleInstance = true;
            if (Instances.Count > 0)
            {
                var item = Instances.First();
                return ActivationAddress.GetAddress(item.Value.SiloAddress, grain, item.Key);
            }
            else
            {
                Instances.Add(act, new ActivationInfo(silo, registrationStatus));
                VersionTag = rand.Next();
                return ActivationAddress.GetAddress(silo, grain, act);
            }
        }

        public bool RemoveActivation(ActivationId act, UnregistrationCause cause, out IActivationInfo info, out bool wasRemoved)
        {
            info = null;
            wasRemoved = false;
            if (Instances.TryGetValue(act, out info) && info.OkToRemove(cause))
            {
                Instances.Remove(act);
                wasRemoved = true;
                VersionTag = rand.Next();
            }
            return Instances.Count == 0;
        }

        public bool Merge(GrainId grain, IGrainInfo other)
        {
            bool modified = false;
            foreach (var pair in other.Instances)
            {
                if (Instances.ContainsKey(pair.Key)) continue;

                Instances[pair.Key] = new ActivationInfo(pair.Value.SiloAddress, pair.Value.RegistrationStatus);
                modified = true;
            }

            if (modified)
            {
                VersionTag = rand.Next();
            }
            
            if (SingleInstance && (Instances.Count > 0))
            {
                // Grain is supposed to be in single activation mode, but we have two activations!!
                // Eventually we should somehow delegate handling this to the silo, but for now, we'll arbitrarily pick one value.
                var orderedActivations = Instances.OrderBy(pair => pair.Key);
                var activationToKeep = orderedActivations.First();
                var activationsToDrop = orderedActivations.Skip(1);
                Instances.Clear();
                Instances.Add(activationToKeep.Key, activationToKeep.Value);
                var list = new List<ActivationAddress>(1);
                foreach (var activation in activationsToDrop.Select(keyValuePair => ActivationAddress.GetAddress(keyValuePair.Value.SiloAddress, grain, keyValuePair.Key)))
                {
                    list.Add(activation);
                    InsideRuntimeClient.Current.InternalGrainFactory.GetSystemTarget<ICatalog>(Constants.CatalogId, activation.Silo).
                        DeleteActivations(list).Ignore();

                    list.Clear();
                }
                return true;
            }
            return false;
        }

        public void CacheOrUpdateRemoteClusterRegistration(GrainId grain, ActivationId oldActivation, ActivationId activation, SiloAddress silo)
        {
            SingleInstance = true;

            if (Instances.Count > 0)
            {
                Instances.Remove(oldActivation);
            }
            Instances.Add(activation, new ActivationInfo(silo, GrainDirectoryEntryStatus.Cached));
        }

        public bool UpdateClusterRegistrationStatus(ActivationId activationId, GrainDirectoryEntryStatus status, GrainDirectoryEntryStatus? compareWith = null)
        {
            IActivationInfo activationInfo;
            if (!Instances.TryGetValue(activationId, out activationInfo))
                return false;
            if (compareWith.HasValue && compareWith.Value != activationInfo.RegistrationStatus)
                return false;
            activationInfo.RegistrationStatus = status;
            return true;
        }
    }

    internal class GrainDirectoryPartition
    {
        // Should we change this to SortedList<> or SortedDictionary so we can extract chunks better for shipping the full
        // parition to a follower, or should we leave it as a Dictionary to get O(1) lookups instead of O(log n), figuring we do
        // a lot more lookups and so can sort periodically?
        /// <summary>
        /// contains a map from grain to its list of activations along with the version (etag) counter for the list
        /// </summary>
        private Dictionary<GrainId, IGrainInfo> partitionData;
        private readonly object lockable;
        private readonly Logger log;
        private ISiloStatusOracle siloStatusOracle;

        [ThreadStatic]
        private static ActivationId[] activationIdsHolder;

        [ThreadStatic]
        private static IActivationInfo[] activationInfosHolder;

        internal int Count { get { return partitionData.Count; } }

        public GrainDirectoryPartition(ISiloStatusOracle siloStatusOracle)
        {
            partitionData = new Dictionary<GrainId, IGrainInfo>();
            lockable = new object();
            log = LogManager.GetLogger("DirectoryPartition");
            this.siloStatusOracle = siloStatusOracle;
        }

        private bool IsValidSilo(SiloAddress silo)
        {
            return this.siloStatusOracle.IsFunctionalDirectory(silo);
        }

        internal void Clear()
        {
            lock (lockable)
            {
                partitionData.Clear();
            }
        }

        /// <summary>
        /// Returns all entries stored in the partition as an enumerable collection
        /// </summary>
        /// <returns></returns>
        public Dictionary<GrainId, IGrainInfo> GetItems()
        {
            lock (lockable)
            {
                return partitionData.Copy();
            }
        }

        /// <summary>
        /// Adds a new activation to the directory partition
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="activation"></param>
        /// <param name="silo"></param>
        /// <returns>The version associated with this directory mapping</returns>
        internal virtual int AddActivation(GrainId grain, ActivationId activation, SiloAddress silo)
        {
            if (!IsValidSilo(silo))
            {
                return GrainInfo.NO_ETAG;
            }

            IGrainInfo grainInfo;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo))
                {
                    partitionData[grain] = grainInfo = new GrainInfo();
                }

                grainInfo.AddActivation(activation, silo);
            }

            if (log.IsVerbose3) log.Verbose3("Adding activation for grain {0}", grain.ToString());
            return grainInfo.VersionTag;
        }

        /// <summary>
        /// Adds a new activation to the directory partition
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="activation"></param>
        /// <param name="silo"></param>
        /// <param name="registrationStatus"></param>
        /// <returns>The registered ActivationAddress and version associated with this directory mapping</returns>
        internal virtual AddressAndTag AddSingleActivation(GrainId grain, ActivationId activation, SiloAddress silo, GrainDirectoryEntryStatus registrationStatus)
        {
            if (log.IsVerbose3) log.Verbose3("Adding single activation for grain {0}{1}{2}", silo, grain, activation);

            AddressAndTag result = new AddressAndTag();

            if (!IsValidSilo(silo))
                return result;

            IGrainInfo grainInfo;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo))
                {
                    partitionData[grain] = grainInfo = new GrainInfo();
                }

                result.Address = grainInfo.AddSingleActivation(grain, activation, silo, registrationStatus);
                result.VersionTag = grainInfo.VersionTag;
            }

            return result;
        }


        /// <summary>
        /// Removes an activation of the given grain from the partition
        /// </summary>
        /// <param name="grain">the identity of the grain</param>
        /// <param name="activation">the id of the activation</param>
        /// <param name="cause">reason for removing the activation</param>
        internal void RemoveActivation(GrainId grain, ActivationId activation, UnregistrationCause cause = UnregistrationCause.Force)
        {
            IActivationInfo ignore1;
            bool ignore2;
            RemoveActivation(grain, activation, cause, out ignore1, out ignore2);
        }


        /// <summary>
        /// Removes an activation of the given grain from the partition
        /// </summary>
        /// <param name="grain">the identity of the grain</param>
        /// <param name="activation">the id of the activation</param>
        /// <param name="cause">reason for removing the activation</param>
        /// <param name="entry">returns the entry, if found </param>
        /// <param name="wasRemoved">returns whether the entry was actually removed</param>
        internal void RemoveActivation(GrainId grain, ActivationId activation, UnregistrationCause cause, out IActivationInfo entry, out bool wasRemoved)
        {
            wasRemoved = false;
            entry = null;
            lock (lockable)
            {
                if (partitionData.ContainsKey(grain) && partitionData[grain].RemoveActivation(activation, cause, out entry, out wasRemoved))
                    // if the last activation for the grain was removed, we remove the entire grain info 
                    partitionData.Remove(grain);

            }
            if (log.IsVerbose3)
                log.Verbose3("Removing activation for grain {0} cause={1} was_removed={2}", grain.ToString(), cause, wasRemoved);
        }

   
        /// <summary>
        /// Removes the grain (and, effectively, all its activations) from the diretcory
        /// </summary>
        /// <param name="grain"></param>
        internal void RemoveGrain(GrainId grain)
        {
            lock (lockable)
            {
                partitionData.Remove(grain);
            }
            if (log.IsVerbose3) log.Verbose3("Removing grain {0}", grain.ToString());
        }

        /// <summary>
        /// Returns a list of activations (along with the version number of the list) for the given grain.
        /// If the grain is not found, null is returned.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        internal AddressesAndTag LookUpActivations(GrainId grain)
        {
            var result = new AddressesAndTag();
            ActivationId[] activationIds;
            IActivationInfo[] activationInfos;
            const int arrayReusingThreshold = 100;
            int grainInfoInstancesCount;

            lock (lockable)
            {
                IGrainInfo graininfo;
                if (!partitionData.TryGetValue(grain, out graininfo))
                {
                    return result;
                }

                result.VersionTag = graininfo.VersionTag;

                grainInfoInstancesCount = graininfo.Instances.Count;
                if (grainInfoInstancesCount < arrayReusingThreshold)
                {
                    if ((activationIds = activationIdsHolder) == null)
                    {
                        activationIdsHolder = activationIds = new ActivationId[arrayReusingThreshold];
                    }

                    if ((activationInfos = activationInfosHolder) == null)
                    {
                        activationInfosHolder = activationInfos = new IActivationInfo[arrayReusingThreshold];
                    }
                }
                else
                {
                    activationIds = new ActivationId[grainInfoInstancesCount];
                    activationInfos = new IActivationInfo[grainInfoInstancesCount];
                }


                graininfo.Instances.Keys.CopyTo(activationIds, 0);
                graininfo.Instances.Values.CopyTo(activationInfos, 0);
            }

            result.Addresses = new List<ActivationAddress>(grainInfoInstancesCount);
            for (var i = 0; i < grainInfoInstancesCount; i++)
            {
                var activationInfo = activationInfos[i];
                if (IsValidSilo(activationInfo.SiloAddress))
                {
                    result.Addresses.Add(ActivationAddress.GetAddress(activationInfo.SiloAddress, grain, activationIds[i]));
                }

                activationInfos[i] = null;
                activationIds[i] = null;
            }

            return result;
        }


        /// <summary>
        /// Returns the activation of a single-activation grain, if present.
        /// </summary>
        internal GrainDirectoryEntryStatus TryGetActivation(GrainId grain, out ActivationAddress address, out int version)
        {
            IGrainInfo grainInfo;
            address = null;
            version = 0;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo))
                {
                    return GrainDirectoryEntryStatus.Invalid;
                }

                var first = grainInfo.Instances.FirstOrDefault();
                if (first.Value != null)
                {
                    address = ActivationAddress.GetAddress(first.Value.SiloAddress, grain, first.Key);
                    version = grainInfo.VersionTag;
                    return first.Value.RegistrationStatus;
                }
            }

            return GrainDirectoryEntryStatus.Invalid;
        }



        /// <summary>
        /// Returns the version number of the list of activations for the grain.
        /// If the grain is not found, -1 is returned.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        internal int GetGrainETag(GrainId grain)
        {
            IGrainInfo grainInfo;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo))
                {
                    return GrainInfo.NO_ETAG;
                }

                return grainInfo.VersionTag;
            }
        }

        /// <summary>
        /// Merges one partition into another, asuuming partitions are disjoint.
        /// This method is supposed to be used by handoff manager to update the partitions when the system view (set of live silos) changes.
        /// </summary>
        /// <param name="other"></param>
        internal void Merge(GrainDirectoryPartition other)
        {
            lock (lockable)
            {
                foreach (var pair in other.partitionData)
                {
                    if (partitionData.ContainsKey(pair.Key))
                    {
                        if (log.IsVerbose) log.Verbose("While merging two disjoint partitions, same grain " + pair.Key + " was found in both partitions");
                        partitionData[pair.Key].Merge(pair.Key, pair.Value);
                    }
                    else
                    {
                        partitionData.Add(pair.Key, pair.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Runs through all entries in the partition and moves/copies (depending on the given flag) the
        /// entries satisfying the given predicate into a new partition.
        /// This method is supposed to be used by handoff manager to update the partitions when the system view (set of live silos) changes.
        /// </summary>
        /// <param name="predicate">filter predicate (usually if the given grain is owned by particular silo)</param>
        /// <param name="modifyOrigin">flag controling whether the source partition should be modified (i.e., the entries should be moved or just copied) </param>
        /// <returns>new grain directory partition containing entries satisfying the given predicate</returns>
        internal GrainDirectoryPartition Split(Predicate<GrainId> predicate, bool modifyOrigin)
        {
            var newDirectory = new GrainDirectoryPartition(this.siloStatusOracle);

            if (modifyOrigin)
            {
                // SInce we use the "pairs" list to modify the underlying collection below, we need to turn it into an actual list here
                List<KeyValuePair<GrainId, IGrainInfo>> pairs;
                lock (lockable)
                {
                    pairs = partitionData.Where(pair => predicate(pair.Key)).ToList();
                }

                foreach (var pair in pairs)
                {
                    newDirectory.partitionData.Add(pair.Key, pair.Value);
                }

                lock (lockable)
                {
                    foreach (var pair in pairs)
                    {
                        partitionData.Remove(pair.Key);
                    }
                }
            }
            else
            {
                lock (lockable)
                {
                    foreach (var pair in partitionData.Where(pair => predicate(pair.Key)))
                    {
                        newDirectory.partitionData.Add(pair.Key, pair.Value);
                    }
                }
            }

            return newDirectory;
        }

        internal List<ActivationAddress> ToListOfActivations(bool singleActivation)
        {
            var result = new List<ActivationAddress>();
            lock (lockable)
            {
                foreach (var pair in partitionData)
                {
                    var grain = pair.Key;
                    if (pair.Value.SingleInstance == singleActivation)
                    {
                        result.AddRange(pair.Value.Instances.Select(activationPair => ActivationAddress.GetAddress(activationPair.Value.SiloAddress, grain, activationPair.Key))
                            .Where(addr => IsValidSilo(addr.Silo)));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Sets the internal parition dictionary to the one given as input parameter.
        /// This method is supposed to be used by handoff manager to update the old partition with a new partition.
        /// </summary>
        /// <param name="newPartitionData">new internal partition dictionary</param>
        internal void Set(Dictionary<GrainId, IGrainInfo> newPartitionData)
        {
            partitionData = newPartitionData;
        }

        /// <summary>
        /// Updates partition with a new delta of changes.
        /// This method is supposed to be used by handoff manager to update the partition with a set of delta changes.
        /// </summary>
        /// <param name="newPartitionDelta">dictionary holding a set of delta updates to this partition.
        /// If the value for a given key in the delta is valid, then existing entry in the partition is replaced.
        /// Otherwise, i.e., if the value is null, the corresponding entry is removed.
        /// </param>
        internal void Update(Dictionary<GrainId, IGrainInfo> newPartitionDelta)
        {
            lock (lockable)
            {
                foreach (GrainId grain in newPartitionDelta.Keys)
                {
                    if (newPartitionDelta[grain] != null)
                    {
                        partitionData[grain] = newPartitionDelta[grain];
                    }
                    else
                    {
                        partitionData.Remove(grain);
                    }
                }
            }
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            lock (lockable)
            {
                foreach (var grainEntry in partitionData)
                {
                    foreach (var activationEntry in grainEntry.Value.Instances)
                    {
                        sb.Append("    ").Append(grainEntry.Key.ToString()).Append("[" + grainEntry.Value.VersionTag + "]").
                            Append(" => ").Append(activationEntry.Key.ToString()).
                            Append(" @ ").AppendLine(activationEntry.Value.ToString());
                    }
                }
            }

            return sb.ToString();
        }

        public void CacheOrUpdateRemoteClusterRegistration(GrainId grain, ActivationId oldActivation, ActivationAddress otherClusterAddress)
        {
            lock (lockable)
            {
                if (partitionData.ContainsKey(grain))
                {
                    partitionData[grain].CacheOrUpdateRemoteClusterRegistration(grain, oldActivation,
                        otherClusterAddress.Activation, otherClusterAddress.Silo);

                }
                else
                {
                    AddSingleActivation(grain, otherClusterAddress.Activation, otherClusterAddress.Silo,
                        GrainDirectoryEntryStatus.Cached);
                }
            }
        }

        public bool UpdateClusterRegistrationStatus(GrainId grain, ActivationId activationId, GrainDirectoryEntryStatus registrationStatus, GrainDirectoryEntryStatus? compareWith = null)
        {
            lock (lockable)
            {
                IGrainInfo graininfo;
                if (partitionData.TryGetValue(grain, out graininfo))
                {
                    return graininfo.UpdateClusterRegistrationStatus(activationId, registrationStatus, compareWith);
                }
                return false;
            }
        }
    }
}