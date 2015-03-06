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

﻿using System.Collections.Generic;
using System.Linq;

using System.Text;
using System;


namespace Orleans.Runtime.GrainDirectory
{
    [Serializable]
    internal class ActivationInfo : IActivationInfo
    {
        public SiloAddress SiloAddress { get; private set; }
        public DateTime TimeCreated { get; private set; }

        public ActivationInfo(SiloAddress siloAddress)
        {
            SiloAddress = siloAddress;
            TimeCreated = DateTime.UtcNow;
        }

        public ActivationInfo(IActivationInfo iActivationInfo)
        {
            SiloAddress = iActivationInfo.SiloAddress;
            TimeCreated = iActivationInfo.TimeCreated;
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
            Instances[act] = new ActivationInfo(silo);
            VersionTag = rand.Next();
            return true;
        }

        public ActivationAddress AddSingleActivation(GrainId grain, ActivationId act, SiloAddress silo)
        {
            SingleInstance = true;
            if (Instances.Count > 0)
            {
                var item = Instances.First();
                return ActivationAddress.GetAddress(item.Value.SiloAddress, grain, item.Key);
            }
            else
            {
                Instances.Add(act, new ActivationInfo(silo));
                VersionTag = rand.Next();
                return ActivationAddress.GetAddress(silo, grain, act);
            }
        }

        public bool RemoveActivation(ActivationAddress addr)
        {
            return RemoveActivation(addr.Activation, true);
        }

        public bool RemoveActivation(ActivationId act, bool force)
        {
            if (force)
            {
                Instances.Remove(act);
                VersionTag = rand.Next();                
            }
            else
            {
                if (Silo.CurrentSilo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay > TimeSpan.Zero)
                {
                    IActivationInfo info;
                    if (Instances.TryGetValue(act, out info))
                    {
                        if (info.TimeCreated >= DateTime.UtcNow - Silo.CurrentSilo.OrleansConfig.Globals.DirectoryLazyDeregistrationDelay)
                        {
                            Instances.Remove(act);
                            VersionTag = rand.Next();
                        }
                    }
                }
            }
            return Instances.Count == 0;
        }

        public bool Merge(GrainId grain, IGrainInfo other)
        {
            bool modified = false;
            foreach (var pair in other.Instances)
            {
                if (Instances.ContainsKey(pair.Key)) continue;

                Instances[pair.Key] = new ActivationInfo(pair.Value.SiloAddress);
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
                    CatalogFactory.GetSystemTarget(Constants.CatalogId, activation.Silo).
                        DeleteActivations(list).Ignore();

                    list.Clear();
                }
                return true;
            }
            return false;
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
        private readonly TraceLogger log;
        private ISiloStatusOracle membership;

        internal int Count { get { return partitionData.Count; } }

        internal GrainDirectoryPartition()
        {
            partitionData = new Dictionary<GrainId, IGrainInfo>();
            lockable = new object();
            log = TraceLogger.GetLogger("DirectoryPartition");
            membership = Silo.CurrentSilo.LocalSiloStatusOracle;
        }

        private bool IsValidSilo(SiloAddress silo)
        {
            if (membership == null)
            {
                membership = Silo.CurrentSilo.LocalSiloStatusOracle;
            }
            return membership.IsValidSilo(silo);
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
            lock (lockable)
            {
                if (!partitionData.ContainsKey(grain))
                {
                    partitionData[grain] = new GrainInfo();
                }
                partitionData[grain].AddActivation(activation, silo);
            }
            if (log.IsVerbose3) log.Verbose3("Adding activation for grain {0}", grain.ToString());
            return partitionData[grain].VersionTag;
        }

        /// <summary>
        /// Adds a new activation to the directory partition
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="activation"></param>
        /// <param name="silo"></param>
        /// <returns>The registered ActivationAddress and version associated with this directory mapping</returns>
        internal virtual Tuple<ActivationAddress, int> AddSingleActivation(GrainId grain, ActivationId activation, SiloAddress silo)
        {
            if (log.IsVerbose3) log.Verbose3("Adding single activation for grain {0}{1}{2}", silo, grain, activation);

            if (!IsValidSilo(silo))
                return null;
            
            ActivationAddress result;
            lock (lockable)
            {
                if (!partitionData.ContainsKey(grain))
                {
                    partitionData[grain] = new GrainInfo();
                }
                result = partitionData[grain].AddSingleActivation(grain, activation, silo);
            }
            return Tuple.Create(result, partitionData[grain].VersionTag);
        }

        /// <summary>
        /// Removes an activation of the given grain from the partition
        /// </summary>
        /// <param name="grain"></param>
        /// <param name="activation"></param>
        /// <param name="force"></param>
        internal void RemoveActivation(GrainId grain, ActivationId activation, bool force)
        {
            lock (lockable)
            {
                if (partitionData.ContainsKey(grain) && partitionData[grain].RemoveActivation(activation, force))
                {
                    partitionData.Remove(grain);
                }
            }
            if (log.IsVerbose3) log.Verbose3("Removing activation for grain {0}", grain.ToString());
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
        internal Tuple<List<Tuple<SiloAddress, ActivationId>>, int> LookUpGrain(GrainId grain)
        {
            lock (lockable)
            {
                if (!partitionData.ContainsKey(grain)) return null;

                var result = new Tuple<List<Tuple<SiloAddress, ActivationId>>, int>(
                    new List<Tuple<SiloAddress, ActivationId>>(), partitionData[grain].VersionTag);

                foreach (var route in partitionData[grain].Instances)
                {
                    if (IsValidSilo(route.Value.SiloAddress))
                    {
                        result.Item1.Add(new Tuple<SiloAddress, ActivationId>(route.Value.SiloAddress, route.Key));
                    }
                }
                return result;
            }
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
                return partitionData.ContainsKey(grain) ? 
                    partitionData[grain].VersionTag : GrainInfo.NO_ETAG;
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
            var newDirectory = new GrainDirectoryPartition();

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
    }
}
