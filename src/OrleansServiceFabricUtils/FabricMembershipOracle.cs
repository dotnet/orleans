using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans;
using Microsoft.Orleans.ServiceFabric.Models;
using Microsoft.Orleans.ServiceFabric.Utilities;
using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    internal class FabricMembershipOracle : IMembershipOracle, IFabricServiceStatusListener, IDisposable
    {
        private readonly object updateLock = new object();
        private readonly Dictionary<SiloAddress, SiloEntry> silos = new Dictionary<SiloAddress, SiloEntry>();
        private readonly ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener> subscribers =
            new ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener>();
        private readonly TimeSpan refreshPeriod = TimeSpan.FromSeconds(30);
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly GlobalConfiguration globalConfig;
        private readonly IFabricServiceSiloResolver fabricServiceSiloResolver;
        private readonly Logger log;

        // Cached collection of active silos.
        private volatile Dictionary<SiloAddress, SiloStatus> activeSilosCache;

        // Cached collection of silos.
        private volatile Dictionary<SiloAddress, SiloStatus> allSilosCache;

        // Cached collection of multicluster gateways.
        private volatile List<SiloAddress> multiClusterSilosCache;

        private Timer timer;
        private DateTime lastRefreshTime;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricMembershipOracle"/> class.
        /// </summary>
        /// <param name="localSiloDetails">The silo which this instance will provide membership information for.</param>
        /// <param name="globalConfig">The cluster configuration.</param>
        /// <param name="fabricServiceSiloResolver">The service resolver which this instance will use.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public FabricMembershipOracle(
            ILocalSiloDetails localSiloDetails,
            GlobalConfiguration globalConfig,
            IFabricServiceSiloResolver fabricServiceSiloResolver,
            Func<string, Logger> loggerFactory)
        {
            this.log = loggerFactory("MembershipOracle");
            this.localSiloDetails = localSiloDetails;
            this.globalConfig = globalConfig;
            this.fabricServiceSiloResolver = fabricServiceSiloResolver;
            this.silos[this.SiloAddress] = new SiloEntry(SiloStatus.Created, this.SiloName);
        }

        /// <summary>
        /// Status of this silo.
        /// </summary>
        public SiloStatus CurrentStatus => this.GetApproximateSiloStatus(this.SiloAddress);

        /// <summary>
        /// Address of this silo.
        /// </summary>
        public SiloAddress SiloAddress => this.localSiloDetails.SiloAddress;

        /// <summary>
        /// Name of this silo.
        /// </summary>
        public string SiloName => this.localSiloDetails.Name;

        /// <summary>
        /// Returns a value indicating the health of this instance.
        /// </summary>
        /// <param name="lastCheckTime">The last time which this participant's health was checked.</param>
        /// <returns><see langword="true"/> if the participant is healthy, <see langword="false"/> otherwise.</returns>
        public bool CheckHealth(DateTime lastCheckTime)
        {
            return this.lastRefreshTime.Add(this.refreshPeriod + this.refreshPeriod) > DateTime.UtcNow;
        }

        /// <summary>
        /// Get a list of silos that are designated to function as gateways.
        /// </summary>
        /// <returns></returns>
        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways()
        {
            var result = this.multiClusterSilosCache;
            if (result != null) return result;

            lock (this.updateLock)
            {
                if (this.multiClusterSilosCache != null) return this.multiClusterSilosCache;

                // Take all the active silos if their count does not exceed the desired number of gateways
                var maxSize = this.globalConfig.MaxMultiClusterGateways;
                result = new List<SiloAddress>(this.silos.Keys);

                // Restrict the length to the maximum size.
                if (result.Count > maxSize)
                {
                    result.Sort();
                    result.RemoveRange(maxSize, result.Count - maxSize);
                }

                this.multiClusterSilosCache = result;
                return this.multiClusterSilosCache;
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
            var allSilos = this.GetApproximateSiloStatuses(onlyActive: false);
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
                var cachedActive = this.activeSilosCache;
                if (cachedActive != null) return cachedActive;

                // Rebuild the cache, since it's not valid.
                lock (this.updateLock)
                {
                    return this.activeSilosCache ??
                           (this.activeSilosCache =
                               this.silos.Where(s => s.Value.Status == SiloStatus.Active)
                                   .ToDictionary(kv => kv.Key, kv => kv.Value.Status));
                }
            }

            // Return all silos.
            var cachedSilos = this.allSilosCache;
            if (cachedSilos != null) return cachedSilos;

            // Rebuild the cache, since it's not valid.
            lock (this.updateLock)
            {
                return this.allSilosCache ??
                       (this.allSilosCache =
                           this.silos.ToDictionary(kv => kv.Key, kv => kv.Value.Status));
            }
        }

        /// <summary>
        /// Determine if the current silo is dead.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        public bool IsDeadSilo(SiloAddress address)
        {
            if (address.Equals(this.SiloAddress)) return false;
            var status = this.GetApproximateSiloStatus(address);
            return status == SiloStatus.Dead;
        }

        /// <summary>
        /// Determine if the current silo is valid for creating new activations on or for directory lookups.
        /// </summary>
        /// <returns>The silo so ask about.</returns>
        public bool IsFunctionalDirectory(SiloAddress siloAddress)
        {
            if (siloAddress.Equals(this.SiloAddress)) return true;

            var status = this.GetApproximateSiloStatus(siloAddress);
            return !status.IsTerminating();
        }

        /// <summary>
        /// Start this oracle. Will register this silo in the SiloDirectory with SiloStatus.Starting status.
        /// </summary>
        public Task Start()
        {
            this.timer = new Timer(
                self => ((FabricMembershipOracle)self).RefreshAsync().Ignore(),
                this,
                this.refreshPeriod,
                this.refreshPeriod);
            this.fabricServiceSiloResolver.Subscribe(this);
            this.RefreshAsync().Ignore();
            this.UpdateStatus(SiloStatus.Joining);
            return Task.FromResult(0);
        }

        /// <summary>
        /// Turns this oracle into an Active state. Will update this silo in the SiloDirectory with SiloStatus.Active status.
        /// </summary>
        public Task BecomeActive()
        {
            this.UpdateStatus(SiloStatus.Active);
            return Task.FromResult(0);
        }

        /// <summary>
        /// ShutDown this oracle. Will update this silo in the SiloDirectory with SiloStatus.ShuttingDown status. 
        /// </summary>
        public Task ShutDown()
        {
            this.StopInternal();
            this.UpdateStatus(SiloStatus.ShuttingDown);
            return Task.FromResult(0);
        }

        /// <summary>
        /// Stop this oracle. Will update this silo in the SiloDirectory with SiloStatus.Stopping status. 
        /// </summary>
        public Task Stop()
        {
            this.StopInternal();
            this.UpdateStatus(SiloStatus.Stopping);
            return Task.FromResult(0);
        }

        /// <summary>
        /// Completely kill this oracle. Will update this silo in the SiloDirectory with SiloStatus.Dead status. 
        /// </summary>
        public Task KillMyself()
        {
            this.fabricServiceSiloResolver.Unsubscribe(this);
            this.UpdateStatus(SiloStatus.Dead);
            return Task.FromResult(0);
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
            var result = this.silos.TryGetValue(siloAddress, out entry);
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
            this.subscribers[observer] = observer;
            return true;
        }

        /// <summary>
        /// UnSubscribe from status events about all silos. 
        /// </summary>
        /// <returns>bool value indicating that subscription succeeded or not.</returns>
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            this.subscribers.TryRemove(observer, out observer);
            return true;
        }

        /// <summary>
        /// Notifies this instance of an update to one or more partitions.
        /// </summary>
        /// <param name="partitions">The updated set of partitions.</param>
        public void OnUpdate(FabricSiloInfo[] partitions)
        {
            var added = default(HashSet<SiloAddress>);
            var removed = default(HashSet<SiloAddress>);
            lock (this.updateLock)
            {
                foreach (var updatedSilo in partitions)
                {
                    // Update the silo if it was not previously seen or if the existing entry's status
                    // does not match the updated status.
                    SiloEntry existing;
                    if (!this.silos.TryGetValue(updatedSilo.SiloAddress, out existing))
                    {
                        // Add the new silo.
                        if (added == null) added = new HashSet<SiloAddress>();
                        added.Add(updatedSilo.SiloAddress);
                        this.silos[updatedSilo.SiloAddress] = new SiloEntry(SiloStatus.Active, updatedSilo.Name)
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
                foreach (var silo in this.silos)
                {
                    // Never remove self.
                    if (silo.Key.Equals(this.SiloAddress)) continue;

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
                        this.silos.Remove(silo);
                    }
                }
            }

            // If anything was updated, clear the cache before notifying clients.
            if (added != null || removed != null)
            {
                // Clear the caches.
                this.ClearCaches();

                // If any silos were added or removed, notify subscribers of the new status.
                var siloStatusListeners = this.subscribers.Values.ToList();
                if (added != null)
                {
                    foreach (var address in added)
                    {
                        this.NotifySubscribers(address, SiloStatus.Active, siloStatusListeners);
                    }
                }

                if (removed != null)
                {
                    foreach (var address in removed)
                    {
                        this.NotifySubscribers(address, SiloStatus.Dead, siloStatusListeners);
                    }
                }
            }
        }

        /// <summary>
        /// Clears the cached data.
        /// </summary>
        private void ClearCaches()
        {
            this.activeSilosCache = null;
            this.allSilosCache = null;
            this.multiClusterSilosCache = null;
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
                    this.log.Warn(
                        (int)ErrorCode.ServiceFabric_MembershipOracle_ExceptionNotifyingSubscribers,
                        "Exception notifying subscriber.",
                        exception);
                }
            }
        }

        private async Task RefreshAsync()
        {
            try
            {
                await this.fabricServiceSiloResolver.Refresh();

                this.lastRefreshTime = DateTime.UtcNow;
            }
            catch (Exception exception)
            {
                this.log?.Warn(
                    (int) ErrorCode.ServiceFabric_MembershipOracle_ExceptionRefreshingPartitions,
                    "Exception refreshing partitions.",
                    exception);
                throw;
            }
        }

        /// <summary>
        /// Updates the status of this silo.
        /// </summary>
        /// <param name="status">The updated status.</param>
        private void UpdateStatus(SiloStatus status)
        {
            var updated = false;
            lock (this.updateLock)
            {
                var existing = this.silos[this.SiloAddress];
                if (existing.Status != status)
                {
                    updated = true;
                    this.silos[this.SiloAddress] = new SiloEntry(status, this.SiloName);
                    this.ClearCaches();
                }
            }

            if (updated)
            {
                foreach (var subscriber in this.subscribers.Values)
                {
                    subscriber.SiloStatusChangeNotification(this.SiloAddress, status);
                }
            }
        }

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            this.StopInternal();
        }

        private void StopInternal()
        {
            this.timer?.Dispose();
            this.timer = null;
            this.fabricServiceSiloResolver.Unsubscribe(this);
        }

        private class SiloEntry
        {
            public SiloEntry(SiloStatus status, string name)
            {
                this.Status = status;
                this.Name = name;
            }

            public SiloStatus Status { get; }
            public string Name { get; }

            /// <summary>
            /// Gets or sets a value indicating whether this entry was updated.
            /// </summary>
            public bool Refreshed { get; set; }
        }
    }
}