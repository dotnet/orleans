using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.ServiceFabric.Utilities;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.ServiceFabric;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Cluster membership implementation which uses Serivce Fabric's service discovery system.
    /// </summary>
    internal class FabricMembershipOracle : IMembershipOracle, IFabricServiceStatusListener, IDisposable
    {
        private readonly object updateLock = new object();
        private readonly Dictionary<SiloAddress, SiloEntry> silos = new Dictionary<SiloAddress, SiloEntry>();
        private readonly ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener> subscribers =
            new ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener>();
        private readonly AutoResetEvent notificationEvent = new AutoResetEvent(false);
        private readonly BlockingCollection<StatusChangeNotification> notifications = new BlockingCollection<StatusChangeNotification>();
        private readonly TimeSpan refreshPeriod = TimeSpan.FromSeconds(5);
        private readonly ILocalSiloDetails localSiloDetails;
        private readonly IFabricServiceSiloResolver fabricServiceSiloResolver;
        private readonly ILogger log;
        private readonly UnknownSiloMonitor unknownSiloMonitor;
        private readonly MultiClusterOptions multiClusterOptions;

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
        /// <param name="fabricServiceSiloResolver">The service resolver which this instance will use.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="unknownSiloMonitor">The unknown silo monitor.</param>
        /// <param name="multiClusterOptions">Multi-cluster configuration parameters.</param>
        public FabricMembershipOracle(
            ILocalSiloDetails localSiloDetails,
            IFabricServiceSiloResolver fabricServiceSiloResolver,
            ILogger<FabricMembershipOracle> logger,
            UnknownSiloMonitor unknownSiloMonitor,
            IOptions<MultiClusterOptions> multiClusterOptions)
        {
            this.log = logger;
            this.localSiloDetails = localSiloDetails;
            this.fabricServiceSiloResolver = fabricServiceSiloResolver;
            this.unknownSiloMonitor = unknownSiloMonitor;
            this.multiClusterOptions = multiClusterOptions.Value;
            this.silos[this.SiloAddress] = new SiloEntry(SiloStatus.Created, this.SiloName);
        }

        /// <inheritdoc />
        public SiloStatus CurrentStatus => this.GetApproximateSiloStatus(this.SiloAddress);

        /// <inheritdoc />
        public SiloAddress SiloAddress => this.localSiloDetails.SiloAddress;

        /// <inheritdoc />
        public string SiloName => this.localSiloDetails.Name;

        /// <inheritdoc />
        public bool CheckHealth(DateTime lastCheckTime)
        {
            return this.lastRefreshTime.Add(this.refreshPeriod + this.refreshPeriod) > DateTime.UtcNow;
        }

        /// <inheritdoc />
        public IReadOnlyList<SiloAddress> GetApproximateMultiClusterGateways()
        {
            var result = this.multiClusterSilosCache;
            if (result != null) return result;

            lock (this.updateLock)
            {
                if (this.multiClusterSilosCache != null) return this.multiClusterSilosCache;

                // Take all the active silos if their count does not exceed the desired number of gateways
                var maxSize = this.multiClusterOptions.MaxMultiClusterGateways;
                var gateways = this.silos.Where(entry => entry.Value.Status == SiloStatus.Active).Select(entry => entry.Key);
                result = new List<SiloAddress>(gateways);

                // Restrict the length to the maximum size.
                if (result.Count > maxSize)
                {
                    result.Sort();
                    result.RemoveRange(maxSize, result.Count - maxSize);
                }

                this.multiClusterSilosCache = result;
            }

            this.log.Info($"Local cluster multi-cluster gateways: {string.Join(", ", result)}");
            return result;
        }

        /// <inheritdoc />
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
        {
            var allSilos = this.GetApproximateSiloStatuses(onlyActive: false);
            var exists = allSilos.TryGetValue(siloAddress, out var status);
            if (!exists)
            {
                this.unknownSiloMonitor.ReportUnknownSilo(siloAddress);
            }

            return exists ? status : SiloStatus.None;
        }

        /// <inheritdoc />
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

        /// <inheritdoc />
        public bool IsDeadSilo(SiloAddress address)
        {
            if (address.Equals(this.SiloAddress)) return false;
            var status = this.GetApproximateSiloStatus(address);
            return status == SiloStatus.Dead;
        }

        /// <inheritdoc />
        public bool IsFunctionalDirectory(SiloAddress siloAddress)
        {
            if (siloAddress.Equals(this.SiloAddress)) return true;

            var status = this.GetApproximateSiloStatus(siloAddress);
            return !status.IsUnavailable();
        }

        /// <inheritdoc />
        public Task Start()
        {
            // Start processing notifications.
            Task.Factory.StartNew(
                this.ProcessNotifications,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Current).Ignore();

            this.timer = new Timer(
                self => ((FabricMembershipOracle)self).RefreshAsync().Ignore(),
                this,
                this.refreshPeriod,
                this.refreshPeriod);
            this.fabricServiceSiloResolver.Subscribe(this);
            this.RefreshAsync().Ignore();
            this.UpdateStatus(SiloStatus.Joining);
            
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task BecomeActive()
        {
            this.UpdateStatus(SiloStatus.Active);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ShutDown()
        {
            this.UpdateStatus(SiloStatus.ShuttingDown);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task Stop()
        {
            this.UpdateStatus(SiloStatus.Stopping);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task KillMyself()
        {
            this.UpdateStatus(SiloStatus.Dead);
            this.StopInternal();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
        {
            var result = this.silos.TryGetValue(siloAddress, out var entry);
            siloName = entry?.Name;
            if (!result)
            {
                this.unknownSiloMonitor.ReportUnknownSilo(siloAddress);
            }

            return result;
        }

        /// <inheritdoc />
        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer)
        {
            this.subscribers[observer] = observer;
            return true;
        }

        /// <inheritdoc />
        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer)
        {
            this.subscribers.TryRemove(observer, out observer);
            return true;
        }

        /// <inheritdoc />
        public void OnUpdate(FabricSiloInfo[] partitions)
        {
            var hasChanges = false;
            lock (this.updateLock)
            {
                foreach (var updatedSilo in partitions)
                {
                    // Update the silo if it was not previously seen or if the existing entry's status
                    // does not match the updated status.
                    if (this.silos.TryGetValue(updatedSilo.SiloAddress, out var existing))
                    {
                        // Mark the existing silo as being refreshed.
                        existing.Refreshed = true;

                        if (existing.Status != SiloStatus.Active)
                        {
                            this.log.Error(
                                (int) ErrorCode.ServiceFabric_MembershipOracle_EncounteredUndeadSilo,
                                $"Encountered status update indicating a silo which was previously declared dead is now active. Name: {existing.Name}, Address: {updatedSilo.SiloAddress}");
                        }
                    }
                    else
                    {
                        // Add the new silo.
                        this.notifications.Add(new StatusChangeNotification(updatedSilo.SiloAddress, SiloStatus.Active));
                        this.silos[updatedSilo.SiloAddress] = new SiloEntry(SiloStatus.Active, updatedSilo.Name)
                        {
                            Refreshed = true
                        };
                        hasChanges = true;

                        this.log.Info($"Silo {updatedSilo.SiloAddress} ({updatedSilo.Name}) transitioned from {SiloStatus.None} to {SiloStatus.Active}.");
                    }
                }

                // Identify newly dead silos and reset all refresh flags.
                foreach (var silo in this.silos)
                {
                    // The local silo never dies by this mechanism.
                    if (silo.Key.Equals(this.SiloAddress)) continue;

                    // Silos which were not included in the update must be dead.
                    if (!silo.Value.Refreshed && silo.Value.Status != SiloStatus.Dead)
                    {
                        // Mark the silo as dead and record it so that we can notify subscribers.
                        this.log.Info($"Silo {silo.Key} ({silo.Value.Name}) transitioned from {silo.Value.Status} to {SiloStatus.Dead}.");
                        silo.Value.Status = SiloStatus.Dead;
                        this.notifications.Add(new StatusChangeNotification(silo.Key, SiloStatus.Dead));
                        hasChanges = true;
                    }

                    // Reset the refresh flag.
                    silo.Value.Refreshed = false;
                }

                // Clear the caches.
                this.ClearCaches();

                // Determine dead silos which this silo has seen queries for but have never been seen in an update from Service Fabric.
                foreach (var deadSilo in this.unknownSiloMonitor.DetermineDeadSilos(this.GetApproximateSiloStatuses(true)))
                {
                    if (this.silos.TryGetValue(deadSilo, out var entry))
                    {
                        entry.Status = SiloStatus.Dead;
                    }
                    else
                    {
                        this.silos[deadSilo] = new SiloEntry(SiloStatus.Dead, "unknown");
                    }

                    this.notifications.Add(new StatusChangeNotification(deadSilo, SiloStatus.Dead));
                    hasChanges = true;
                }
            }
            
            // If anything was updated, clear the cache before notifying clients.
            if (hasChanges)
            {
                this.log.Info($"Current cluster members: {string.Join(", ", this.GetApproximateSiloStatuses(true))}");

                // Notify all subscribers.
                this.notificationEvent.Set();
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

        private async Task ProcessNotifications()
        {
            while (!this.notifications.IsAddingCompleted)
            {
                await this.notificationEvent.WaitAsync(TimeSpan.FromMinutes(1));
                while (this.notifications.TryTake(out var notification))
                {
                    foreach (var subscriber in this.subscribers.Values)
                    {
                        try
                        {
                            subscriber.SiloStatusChangeNotification(notification.Silo, notification.Status);
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

        /// <inheritdoc />
        public void Dispose()
        {
            this.StopInternal();
        }

        private void StopInternal()
        {
            this.timer?.Dispose();
            this.timer = null;
            this.fabricServiceSiloResolver.Unsubscribe(this);
            this.notifications.CompleteAdding();
            this.notificationEvent.Set();
        }

        private struct StatusChangeNotification
        {
            public StatusChangeNotification(SiloAddress silo, SiloStatus status)
            {
                this.Silo = silo;
                this.Status = status;
            }

            public SiloAddress Silo { get; }
            public SiloStatus Status { get; }
        }

        private class SiloEntry
        {
            public SiloEntry(SiloStatus status, string name)
            {
                this.Status = status;
                this.Name = name;
            }

            public SiloStatus Status { get; set; }

            public string Name { get; }

            /// <summary>
            /// Gets or sets a value indicating whether this entry was updated.
            /// </summary>
            public bool Refreshed { get; set; }
        }
    }
}