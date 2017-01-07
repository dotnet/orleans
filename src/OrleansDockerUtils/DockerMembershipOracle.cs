using Microsoft.Orleans.Docker.Models;
using Microsoft.Orleans.Docker.Utilities;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Orleans.Docker
{
    internal class DockerMembershipOracle : IMembershipOracle, IDockerStatusListener, IDisposable
    {
        /// <summary>
        /// Status of this silo.
        /// </summary>
        public SiloStatus CurrentStatus => GetApproximateSiloStatus(SiloAddress);

        /// <summary>
        /// Address of this silo.
        /// </summary>
        public SiloAddress SiloAddress => localSiloDetails.SiloAddress;
        
        /// <summary>
        /// Name of this silo.
        /// </summary>
        public string SiloName => localSiloDetails.Name;

        private readonly ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener> subscribers =
            new ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener>();
        private readonly object updateLock = new object();
        private readonly Dictionary<SiloAddress, SiloEntry> silos = new Dictionary<SiloAddress, SiloEntry>();        
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly Logger log;
        private readonly GlobalConfiguration globalConfig;
        private readonly IDockerSiloResolver resolver;

        // Cached collection of active silos.
        private volatile Dictionary<SiloAddress, SiloStatus> activeSilosCache;

        // Cached collection of silos.
        private volatile Dictionary<SiloAddress, SiloStatus> allSilosCache;

        // Cached collection of multicluster gateways.
        private volatile List<SiloAddress> multiClusterSilosCache;
        
        /// <summary>
        /// Initialize a new instance of the <see cref="DockerMembershipOracle"/> class
        /// </summary>
        /// <param name="localSiloDetails">The silo which this instance will provide membership information for.</param>
        /// <param name="globalConfig">The cluster configuration.</param>
        /// <param name="siloResolver">The service resolver which this instance will use.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public DockerMembershipOracle(
            ILocalSiloDetails localSiloDetails,
            GlobalConfiguration globalConfig,
            IDockerSiloResolver siloResolver,
            Func<string, Logger> loggerFactory)
        {
            resolver = siloResolver;
            log = loggerFactory("MembershipOracle");
            silos[SiloAddress] = new SiloEntry(SiloStatus.Created, SiloName);
            this.localSiloDetails = localSiloDetails;
            this.globalConfig = globalConfig;
        }
       
        /// <summary>
        /// Returns a value indicating the health of this instance.
        /// </summary>
        /// <param name="lastCheckTime">The last time which this participant's health was checked.</param>
        /// <returns><see langword="true"/> if the participant is healthy, <see langword="false"/> otherwise.</returns>
        public bool CheckHealth(DateTime lastCheckTime)
        {
            return resolver.LastRefreshTime > DateTime.UtcNow;
        }

        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways()
        {
            var result = multiClusterSilosCache;
            if (result != null) return result;

            lock (updateLock)
            {
                if (multiClusterSilosCache != null) return multiClusterSilosCache;

                // Take all the active silos if their count does not exceed the desired number of gateways
                var maxSize = globalConfig.MaxMultiClusterGateways;
                result = new List<SiloAddress>(silos.Keys);

                // Restrict the length to the maximum size.
                if (result.Count > maxSize)
                {
                    result.Sort();
                    result.RemoveRange(maxSize, result.Count - maxSize);
                }

                multiClusterSilosCache = result;
                return multiClusterSilosCache;
            }
        }

        /// <summary>
        /// Get the status of a given silo. 
        /// This method returns an approximate view on the status of a given silo. 
        /// In particular, this oracle may think the given silo is alive, while it may already have failed.
        /// If this oracle thinks the given silo is dead, it has been authoritatively told so by ISiloDirectory.
        /// </summary>
        /// <param name="siloAddress">A silo whose status we are interested in.</param>
        /// <returns>The status of a given silo.</returns>
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            SiloStatus status;
            var allSilos = GetApproximateSiloStatuses(onlyActive: false);
            var exists = allSilos.TryGetValue(siloAddress, out status);
            return exists ? status : SiloStatus.None;
        }

        /// <summary>
        /// Get the statuses of all silo. 
        /// This method returns an approximate view on the statuses of all silo.
        /// </summary>
        /// <param name="onlyActive">Include only silo who are currently considered to be active. If false, include all.</param>
        /// <returns>A list of silo statuses.</returns>
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
        {
            if (onlyActive)
            {
                // Return just the active silos.
                var cachedActive = activeSilosCache;
                if (cachedActive != null) return cachedActive;

                // Rebuild the cache, since it's not valid.
                lock (updateLock)
                {
                    return activeSilosCache ??
                           (activeSilosCache =
                               silos.Where(s => s.Value.Status == SiloStatus.Active)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value.Status));
                }
            }

            // Return all silos.
            var cachedSilos = allSilosCache;
            if (cachedSilos != null) return cachedSilos;

            // Rebuild the cache, since it's not valid.
            lock (updateLock)
            {
                return allSilosCache ??
                       (allSilosCache =
                           silos.ToDictionary(kv => kv.Key, kv => kv.Value.Status));
            }
        }

        /// <summary>
        /// Determine if the current silo is dead.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        public bool IsDeadSilo(SiloAddress address)
        {
            if (address.Equals(SiloAddress)) return false;
            var status = GetApproximateSiloStatus(address);
            return status == SiloStatus.Dead;
        }

        /// <summary>
        /// Determine if the current silo is valid for creating new activations on or for directory lookups.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        public bool IsFunctionalDirectory(SiloAddress siloAddress)
        {
            if (siloAddress.Equals(SiloAddress)) return true;

            var status = GetApproximateSiloStatus(siloAddress);
            return !status.IsTerminating();
        }

        /// <summary>
        /// Start this oracle. Will register this silo in the SiloDirectory with SiloStatus.Starting status.
        /// </summary>
        public Task Start()
        {
            resolver.Subscribe(this);
            UpdateStatus(SiloStatus.Joining);

            return TaskDone.Done;
        }

        /// <summary>
        /// Turns this oracle into an Active state. Will update this silo in the SiloDirectory with SiloStatus.Active status.
        /// </summary>
        public Task BecomeActive()
        {
            UpdateStatus(SiloStatus.Active);
            return TaskDone.Done;
        }

        /// <summary>
        /// Completely kill this oracle. Will update this silo in the SiloDirectory with SiloStatus.Dead status. 
        /// </summary>
        public Task KillMyself()
        {
            resolver.Unsubscribe(this);
            UpdateStatus(SiloStatus.Dead);
            return TaskDone.Done;
        }

        /// <summary>
        /// ShutDown this oracle. Will update this silo in the SiloDirectory with SiloStatus.ShuttingDown status. 
        /// </summary>
        public Task ShutDown()
        {
            StopInternal();
            UpdateStatus(SiloStatus.ShuttingDown);
            return TaskDone.Done;
        }
        
        /// <summary>
        /// Stop this oracle. Will update this silo in the SiloDirectory with SiloStatus.Stopping status. 
        /// </summary>
        public Task Stop()
        {
            StopInternal();
            UpdateStatus(SiloStatus.Stopping);
            return TaskDone.Done;
        }

        /// <summary>
        /// Get the name of a silo. 
        /// Silo name is assumed to be static and does not change across restarts of the same silo.
        /// </summary>
        /// <param name="siloAddress">A silo whose name we are interested in.</param>
        /// <param name="siloName">A silo name.</param>
        /// <returns>TTrue if could return the requested name, false otherwise.</returns>
        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            SiloEntry entry;
            var result = silos.TryGetValue(siloAddress, out entry);
            siloName = entry?.Name;
            return result;
        }

        /// <summary>
        /// Subscribe to status events about all silos. 
        /// </summary>
        /// <param name="observer">An observer async interface to receive silo status notifications.</param>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            subscribers[observer] = observer;
            return true;
        }

        /// <summary>
        /// UnSubscribe from status events about all silos. 
        /// </summary>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            subscribers.TryRemove(observer, out observer);
            return true;
        }
        
        /// <summary>
        /// Notifies this instance of an update to one or more partitions.
        /// </summary>
        /// <param name="newSilos">The updated set of partitions.</param>
        public void OnUpdate(DockerSiloInfo[] newSilos)
        {
            var added = default(HashSet<SiloAddress>);
            var removed = default(HashSet<SiloAddress>);
            lock (updateLock)
            {
                foreach (var updatedSilo in newSilos)
                {
                    // Update the silo if it was not previously seen or if the existing entry's status
                    // does not match the updated status.
                    SiloEntry existing;
                    if (!silos.TryGetValue(updatedSilo.SiloAddress, out existing))
                    {
                        // Add the new silo.
                        if (added == null) added = new HashSet<SiloAddress>();
                        added.Add(updatedSilo.SiloAddress);
                        silos[updatedSilo.SiloAddress] = new SiloEntry(SiloStatus.Active, updatedSilo.Name)
                        {
                            Refreshed = true
                        };
                    }
                    else
                    {
                        // Mark the existing silo as being refreshed.
                        existing.Refreshed = true;
                    }
                }

                // Identify removed silos.
                foreach (var silo in silos)
                {
                    // Never remove self.
                    if (silo.Key.Equals(SiloAddress)) continue;

                    // Do not remove silos which were present in the update.
                    if (silo.Value.Refreshed)
                    {
                        // Reset the refresh flag.
                        silo.Value.Refreshed = false;
                        continue;
                    }

                    // The silo was not present in the update, remove it.
                    if (removed == null) removed = new HashSet<SiloAddress>();
                    removed.Add(silo.Key);
                }

                // If any silos were removed, 
                if (removed != null)
                {
                    foreach (var silo in removed)
                    {
                        silos.Remove(silo);
                    }
                }
            }

            // If anything was updated, clear the cache before notifying clients.
            if (added != null || removed != null)
            {
                // Clear the caches.
                ClearCaches();

                // If any silos were added or removed, notify subscribers of the new status.
                var siloStatusListeners = subscribers.Values.ToList();
                if (added != null)
                {
                    foreach (var address in added)
                    {
                        NotifySubscribers(address, SiloStatus.Active, siloStatusListeners);
                    }
                }

                if (removed != null)
                {
                    foreach (var address in removed)
                    {
                        NotifySubscribers(address, SiloStatus.Dead, siloStatusListeners);
                    }
                }
            }
        }

        /// <summary>
        /// Clears the cached data.
        /// </summary>
        private void ClearCaches()
        {
            activeSilosCache = null;
            allSilosCache = null;
            multiClusterSilosCache = null;
        }

        /// <summary>
        /// Updates the status of this silo.
        /// </summary>
        /// <param name="status">The updated status.</param>
        private void UpdateStatus(SiloStatus status)
        {
            var updated = false;
            lock (updateLock)
            {
                var existing = silos[SiloAddress];
                if (existing.Status != status)
                {
                    updated = true;
                    silos[SiloAddress] = new SiloEntry(status, SiloName);
                    ClearCaches();
                }
            }

            if (updated)
            {
                foreach (var subscriber in subscribers.Values)
                {
                    subscriber.SiloStatusChangeNotification(SiloAddress, status);
                }
            }
        }

        private void NotifySubscribers(SiloAddress address, SiloStatus newStatus, List<ISiloStatusListener> listeners)
        {
            foreach (var subscriber in listeners)
            {
                try
                {
                    subscriber.SiloStatusChangeNotification(address, newStatus);
                }
                catch (Exception exception)
                {
                    log.Warn(
                        (int)ErrorCode.Docker_MembershipOracle_ExceptionNotifyingSubscribers,
                        "Exception notifying subscriber.",
                        exception);
                }
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            StopInternal();
        }

        private void StopInternal()
        {            
            resolver.Unsubscribe(this);
        }

        private class SiloEntry
        {
            /// <summary>
            /// Gets or sets a value indicating whether this entry was updated.
            /// </summary>
            public bool Refreshed { get; set; }

            public SiloEntry(SiloStatus status, string name)
            {
                Status = status;
                Name = name;
            }

            public SiloStatus Status { get; }
            public string Name { get; }          
        }
    }
}
