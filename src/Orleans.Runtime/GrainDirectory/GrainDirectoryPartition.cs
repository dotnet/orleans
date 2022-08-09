using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    [Serializable]
    internal sealed class GrainInfo
    {
        public const int NO_ETAG = -1;

        public GrainAddress? Activation { get; private set; }

        public DateTime TimeCreated { get; private set; }

        public int VersionTag { get; private set; }

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

        public GrainAddress TryAddSingleActivation(GrainAddress address)
        {
            if (Activation is { } existing)
            {
                return existing;
            }
            else
            {
                Activation = address;
                TimeCreated = DateTime.UtcNow;
                VersionTag = Random.Shared.Next();
                return address;
            }
        }

        public bool RemoveActivation(ActivationId act, UnregistrationCause cause, TimeSpan lazyDeregistrationDelay, out bool wasRemoved)
        {
            wasRemoved = false;
            if (Activation is { } existing && existing.ActivationId.Equals(act) && OkToRemove(cause, lazyDeregistrationDelay))
            {
                wasRemoved = true;
                Activation = null;
                VersionTag = Random.Shared.Next();
            }

            return wasRemoved;
        }

        public GrainAddress? Merge(GrainInfo other)
        {
            var otherActivation = other.Activation;
            if (otherActivation is not null && Activation is null)
            {
                Activation = other.Activation;
                TimeCreated = other.TimeCreated;
                VersionTag = Random.Shared.Next();
            }
            else if (Activation is not null && otherActivation is not null)
            {
                // Grain is supposed to be in single activation mode, but we have two activations!!
                // Eventually we should somehow delegate handling this to the silo, but for now, we'll arbitrarily pick one value.
                if (Activation.ActivationId.Key.CompareTo(otherActivation.ActivationId.Key) < 0)
                {
                    var activationToDrop = Activation;
                    Activation = otherActivation;
                    TimeCreated = other.TimeCreated;
                    VersionTag = Random.Shared.Next();
                    return activationToDrop;
                }

                // Keep this activation and destroy the other.
                return otherActivation;
            }

            return null;
        }
    }

    internal sealed class GrainDirectoryPartition
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
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IOptions<GrainDirectoryOptions> grainDirectoryOptions;

        internal int Count { get { return partitionData.Count; } }

        public GrainDirectoryPartition(ISiloStatusOracle siloStatusOracle, IOptions<GrainDirectoryOptions> grainDirectoryOptions, ILoggerFactory loggerFactory)
        {
            partitionData = new Dictionary<GrainId, GrainInfo>();
            lockable = new object();
            log = loggerFactory.CreateLogger<GrainDirectoryPartition>();
            this.siloStatusOracle = siloStatusOracle;
            this.grainDirectoryOptions = grainDirectoryOptions;
        }

        private bool IsValidSilo(SiloAddress? silo) => siloStatusOracle.IsFunctionalDirectory(silo);

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
        /// <returns>The registered ActivationAddress and version associated with this directory mapping</returns>
        internal AddressAndTag AddSingleActivation(GrainAddress address)
        {
            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Adding single activation for grain {SiloAddress} {GrainId} {ActivationId}", address.SiloAddress, address.GrainId, address.ActivationId);

            if (!IsValidSilo(address.SiloAddress))
            {
                var siloStatus = this.siloStatusOracle.GetApproximateSiloStatus(address.SiloAddress);
                throw new OrleansException($"Trying to register {address.GrainId} on invalid silo: {address.SiloAddress}. Known status: {siloStatus}");
            }

            AddressAndTag result;
            lock (lockable)
            {
                if (!partitionData.TryGetValue(address.GrainId, out var grainInfo))
                {
                    partitionData[address.GrainId] = grainInfo = new GrainInfo();
                }
                else
                {
                    var siloAddress = grainInfo.Activation?.SiloAddress;
                    // If there is an existing entry pointing to an invalid silo then remove it 
                    if (siloAddress != null && !IsValidSilo(siloAddress))
                    {
                        partitionData[address.GrainId] = grainInfo = new GrainInfo();
                    }
                }

                result.Address = grainInfo.TryAddSingleActivation(address);
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
            var wasRemoved = false;
            lock (lockable)
            {
                if (partitionData.TryGetValue(grain, out var value) && value.RemoveActivation(activation, cause, this.grainDirectoryOptions.Value.LazyDeregistrationDelay, out wasRemoved))
                {
                    // if the last activation for the grain was removed, we remove the entire grain info 
                    partitionData.Remove(grain);
                }
            }

            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Removing activation for grain {GrainId} cause={Cause} was_removed={WasRemoved}", grain.ToString(), cause, wasRemoved);
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
            if (log.IsEnabled(LogLevel.Trace)) log.LogTrace("Removing grain {GrainId}", grain.ToString());
        }

        internal AddressAndTag LookUpActivation(GrainId grain)
        {
            var result = new AddressAndTag();
            lock (lockable)
            {
                if (!partitionData.TryGetValue(grain, out var grainInfo) || grainInfo.Activation is null)
                {
                    return result;
                }

                result.Address = grainInfo.Activation;
                result.VersionTag = grainInfo.VersionTag;
            }

            if (!IsValidSilo(result.Address.SiloAddress))
            {
                result.Address = null;
            }

            return result;
        }

        /// <summary>
        /// Returns the version number of the list of activations for the grain.
        /// If the grain is not found, -1 is returned.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        internal int GetGrainETag(GrainId grain)
        {
            lock (lockable)
            {
                return partitionData.TryGetValue(grain, out var info) ? info.VersionTag : GrainInfo.NO_ETAG;
            }
        }

        /// <summary>
        /// Merges one partition into another, assuming partitions are disjoint.
        /// This method is supposed to be used by handoff manager to update the partitions when the system view (set of live silos) changes.
        /// </summary>
        /// <param name="other"></param>
        /// <returns>Activations which must be deactivated.</returns>
        internal Dictionary<SiloAddress, List<GrainAddress>>? Merge(GrainDirectoryPartition other)
        {
            Dictionary<SiloAddress, List<GrainAddress>>? activationsToRemove = null;
            lock (lockable)
            {
                foreach (var pair in other.partitionData)
                {
                    ref var grainInfo = ref CollectionsMarshal.GetValueRefOrAddDefault(partitionData, pair.Key, out _);
                    if (grainInfo is { } existing)
                    {
                        if (log.IsEnabled(LogLevel.Debug))
                        {
                            log.LogDebug("While merging two disjoint partitions, same grain {GrainId} was found in both partitions", pair.Key);
                        }

                        var activationToDrop = existing.Merge(pair.Value);
                        if (activationToDrop == null) continue;
                        (CollectionsMarshal.GetValueRefOrAddDefault(activationsToRemove ??= new(), activationToDrop.SiloAddress!, out _) ??= new()).Add(activationToDrop);
                    }
                    else
                    {
                        grainInfo = pair.Value;
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
        /// <returns>Entries satisfying the given predicate</returns>
        internal List<GrainAddress> Split(Predicate<GrainId> predicate)
        {
            var result = new List<GrainAddress>();

            lock (lockable)
            {
                foreach (var pair in partitionData)
                {
                    if (pair.Value.Activation is { } address && predicate(pair.Key))
                    {
                        result.Add(address);
                    }
                }
            }

            for (var i = result.Count - 1; i >= 0; i--)
            {
                if (!IsValidSilo(result[i].SiloAddress))
                {
                    result.RemoveAt(i);
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
                    if (grainEntry.Value.Activation is { } activation)
                    {
                        sb.Append("    ").Append(grainEntry.Key.ToString()).Append("[" + grainEntry.Value.VersionTag + "]").
                            Append(" => ").Append(activation.GrainId.ToString()).
                            Append(" @ ").AppendLine(activation.ToString());
                    }
                }
            }

            return sb.ToString();
        }
    }
}