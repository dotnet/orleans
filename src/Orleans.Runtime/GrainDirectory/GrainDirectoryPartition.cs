using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Internal;

namespace Orleans.Runtime.GrainDirectory
{
    [Serializable]
    internal class GrainInfo
    {
        public ActivationId ActivationId { get; private set; }
        public SiloAddress SiloAddress { get; private set; }
        public DateTime TimeCreated { get; private set; }
        public int VersionTag { get; private set; }

        private static readonly SafeRandom rand;

        static GrainInfo()
        {
            rand = new SafeRandom();
        }

        internal GrainInfo(ActivationId activationId, SiloAddress siloAddress)
        {
            ActivationId = activationId;
            SiloAddress = siloAddress;
            VersionTag = rand.Next();
            TimeCreated = DateTime.UtcNow;
        }

        public (SiloAddress SiloAddress, ActivationId ActivationId) Merge(GrainInfo other)
        {
            var comparison = ActivationId.Key.CompareTo(other.ActivationId.Key);
            if (comparison == 0)
            {
                return default;
            }
            
            if (comparison > 0)
            {
                // Drop the other registration
                return (other.SiloAddress, other.ActivationId);
            }

            var previousValue = (SiloAddress, ActivationId);
            
            // Clone the other registration into this one
            SiloAddress = other.SiloAddress;
            ActivationId = other.ActivationId;
            TimeCreated = other.TimeCreated;
            VersionTag = other.VersionTag;

            // Drop the previous values stored in this registration
            return previousValue; 
        }

        public bool OkToRemove(UnregistrationCause cause, TimeSpan lazyDeregistrationDelay)
        {
            switch (cause)
            {
                case UnregistrationCause.Force:
                    return true;

                case UnregistrationCause.NonexistentActivation:
                    {
                        var delayparameter = lazyDeregistrationDelay;
                        if (delayparameter <= TimeSpan.Zero)
                            return false; // no lazy deregistration
                        else
                            return (TimeCreated <= DateTime.UtcNow - delayparameter);
                    }

                default:
                    throw new OrleansException("unhandled case");
            }
        }

        public override string ToString() => $"{ActivationId}, {SiloAddress}, {VersionTag}, {TimeCreated}";
    }

    internal class GrainDirectoryPartition
    {
        // Should we change this to SortedList<> or SortedDictionary so we can extract chunks better for shipping the full
        // parition to a follower, or should we leave it as a Dictionary to get O(1) lookups instead of O(log n), figuring we do
        // a lot more lookups and so can sort periodically?
        /// <summary>
        /// contains a map from grain to its list of activations along with the version (etag) counter for the list
        /// </summary>
        private Dictionary<GrainId, GrainInfo> partitionData;
        private readonly object lockable;
        private readonly ILogger log;
        private readonly ILoggerFactory loggerFactory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly IOptions<GrainDirectoryOptions> grainDirectoryOptions;

        internal int Count { get { return partitionData.Count; } }

        public GrainDirectoryPartition(ISiloStatusOracle siloStatusOracle, IOptions<GrainDirectoryOptions> grainDirectoryOptions, IInternalGrainFactory grainFactory, ILoggerFactory loggerFactory)
        {
            partitionData = new Dictionary<GrainId, GrainInfo>();
            lockable = new object();
            log = loggerFactory.CreateLogger<GrainDirectoryPartition>();
            this.siloStatusOracle = siloStatusOracle;
            this.grainDirectoryOptions = grainDirectoryOptions;
            this.grainFactory = grainFactory;
            this.loggerFactory = loggerFactory;
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
        public List<KeyValuePair<GrainId, GrainInfo>> GetItems()
        {
            lock (lockable)
            {
                return partitionData.ToList();
            }
        }

        /// <summary>
        /// Adds a new activation to the directory partition
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="activation"></param>
        /// <param name="silo"></param>
        /// <returns>The registered ActivationAddress and version associated with this directory mapping</returns>
        internal virtual AddressAndTag AddSingleActivation(GrainId grain, ActivationId activation, SiloAddress silo)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Adding single activation for grain {0}{1}{2}", silo, grain, activation);

            if (!IsValidSilo(silo))
            {
                var siloStatus = this.siloStatusOracle.GetApproximateSiloStatus(silo);
                throw new OrleansException($"Trying to register {grain} on invalid silo: {silo}. Known status: {siloStatus}");
            }

            GrainInfo grainInfo;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo) || !IsValidSilo(grainInfo.SiloAddress))
                {
                    partitionData[grain] = grainInfo = new GrainInfo(activation, silo);
                }
            }

            return new AddressAndTag
            {
                Address = ActivationAddress.GetAddress(grainInfo.SiloAddress, grain, grainInfo.ActivationId),
                VersionTag = grainInfo.VersionTag
            };
        }


        /// <summary>
        /// Removes an activation of the given grain from the partition
        /// </summary>
        /// <param name="grain">the identity of the grain</param>
        /// <param name="activation">the id of the activation</param>
        /// <param name="cause">reason for removing the activation</param>
        internal void RemoveActivation(GrainId grain, ActivationId activation, UnregistrationCause cause = UnregistrationCause.Force)
        {
            var wasRemoved = false;
            lock (lockable)
            {
                if (partitionData.TryGetValue(grain, out var existing)
                    && activation.Equals(existing.ActivationId)
                    && existing.OkToRemove(cause, this.grainDirectoryOptions.Value.LazyDeregistrationDelay))
                {
                    // if the activation was removed, we remove the entire grain info 
                    partitionData.Remove(grain);
                    wasRemoved = true;
                }
            }

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Removing activation for grain {0} cause={1} was_removed={2}", grain.ToString(), cause, wasRemoved);
        }
   
        /// <summary>
        /// Removes the grain (and, effectively, all its activations) from the directory
        /// </summary>
        /// <param name="grain"></param>
        internal void RemoveGrain(GrainId grain)
        {
            lock (lockable)
            {
                partitionData.Remove(grain);
            }

            if (log.IsEnabled(LogLevel.Trace)) log.Trace("Removing grain {0}", grain.ToString());
        }

        /// <summary>
        /// Returns a list of activations (along with the version number of the list) for the given grain.
        /// If the grain is not found, null is returned.
        /// </summary>
        internal bool TryLookup(GrainId grain, out AddressAndTag result)
        {
            GrainInfo grainInfo;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out grainInfo))
                {
                    result = default;
                    return false;
                }
            }

            if (IsValidSilo(grainInfo.SiloAddress))
            {
                result = new AddressAndTag
                {
                    Address = ActivationAddress.GetAddress(grainInfo.SiloAddress, grain, grainInfo.ActivationId),
                    VersionTag = grainInfo.VersionTag
                };

                return true;
            }

            result = default;
            return false;
        }

        /// <summary>
        /// Merges one partition into another, assuming partitions are disjoint.
        /// This method is supposed to be used by handoff manager to update the partitions when the system view (set of live silos) changes.
        /// </summary>
        /// <param name="other"></param>
        /// <returns>Activations which must be deactivated.</returns>
        internal Dictionary<SiloAddress, List<ActivationAddress>> Merge(GrainDirectoryPartition other)
        {
            Dictionary<SiloAddress, List<ActivationAddress>> activationsToRemove = null;
            lock (lockable)
            {
                foreach (var pair in other.partitionData)
                {
                    if (partitionData.TryGetValue(pair.Key, out var existing))
                    {
                        if (log.IsEnabled(LogLevel.Debug)) log.Debug("While merging two disjoint partitions, same grain " + pair.Key + " was found in both partitions");
                        var dropped = existing.Merge(pair.Value);
                        if (dropped.SiloAddress is null)
                        {
                            // This would happen if both registrations were for the same activation.
                            continue;
                        }

                        activationsToRemove ??= new Dictionary<SiloAddress, List<ActivationAddress>>();
                        if (!activationsToRemove.TryGetValue(dropped.SiloAddress, out var activations))
                        {
                            activationsToRemove[dropped.SiloAddress] = activations = new List<ActivationAddress>();
                        }

                        activations.Add(ActivationAddress.GetAddress(dropped.SiloAddress, pair.Key, dropped.ActivationId));
                    }
                    else
                    {
                        partitionData.Add(pair.Key, pair.Value);
                    }
                }
            }

            return activationsToRemove;
        }

        /// <summary>
        /// Runs through all entries in the partition and moves/copies (depending on the given flag) the
        /// entries satisfying the given predicate into a new partition.
        /// This method is supposed to be used by handoff manager to update the partitions when the system view (set of live silos) changes.
        /// </summary>
        /// <param name="predicate">filter predicate (usually if the given grain is owned by particular silo)</param>
        /// <param name="modifyOrigin">flag controlling whether the source partition should be modified (i.e., the entries should be moved or just copied) </param>
        /// <returns>new grain directory partition containing entries satisfying the given predicate</returns>
        internal GrainDirectoryPartition Split(Predicate<GrainId> predicate, bool modifyOrigin)
        {
            var newDirectory = new GrainDirectoryPartition(this.siloStatusOracle, this.grainDirectoryOptions, this.grainFactory, this.loggerFactory);

            if (modifyOrigin)
            {
                // SInce we use the "pairs" list to modify the underlying collection below, we need to turn it into an actual list here
                List<KeyValuePair<GrainId, GrainInfo>> pairs;
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

        internal List<ActivationAddress> ToListOfActivations()
        {
            var result = new List<ActivationAddress>();
            lock (lockable)
            {
                foreach (var pair in partitionData)
                {
                    var grain = pair.Key;
                    var info = pair.Value;
                    if (info.SiloAddress is SiloAddress silo && IsValidSilo(silo))
                    {
                        result.Add(ActivationAddress.GetAddress(info.SiloAddress, grain, info.ActivationId));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Sets the internal partition dictionary to the one given as input parameter.
        /// This method is supposed to be used by handoff manager to update the old partition with a new partition.
        /// </summary>
        /// <param name="newPartitionData">new internal partition dictionary</param>
        internal void Set(Dictionary<GrainId, GrainInfo> newPartitionData)
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
        internal void Update(Dictionary<GrainId, GrainInfo> newPartitionDelta)
        {
            lock (lockable)
            {
                foreach (var kv in newPartitionDelta)
                {
                    if (kv.Value != null)
                    {
                        partitionData[kv.Key] = kv.Value;
                    }
                    else
                    {
                        partitionData.Remove(kv.Key);
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
                    var activationEntry = grainEntry.Value;
                    sb.Append("    ").Append(grainEntry.Key.ToString()).Append("[" + grainEntry.Value.VersionTag + "]").
                        Append(" => ").Append(activationEntry.ActivationId.ToString()).
                        Append(" @ ").AppendLine(activationEntry.SiloAddress.ToString());
                }
            }

            return sb.ToString();
        }
    }
}