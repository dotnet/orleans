using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Clustering.ServiceFabric.Models;
using Orleans.Clustering.ServiceFabric.Utilities;
using Orleans.Runtime;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Provides information about an Orleans cluster hosted on Service Fabric.
    /// </summary>
    internal class FabricServiceSiloResolver : IFabricServiceSiloResolver
    {
        private readonly Uri serviceName;
        private readonly IFabricQueryManager queryManager;
        private readonly ILogger log;
        private readonly object updateLock = new object();
        private readonly ConcurrentDictionary<IFabricServiceStatusListener, IFabricServiceStatusListener> subscribers =
            new ConcurrentDictionary<IFabricServiceStatusListener, IFabricServiceStatusListener>();

        private readonly FabricPartitionResolutionChangeHandler partitionChangeHandler;
        private ServicePartitionSilos[] silos;
        private Dictionary<Guid, long> changeHandlerRegistrations;

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricServiceSiloResolver"/> class.
        /// </summary>
        /// <param name="serviceName">The name of the Service Fabric service which this instance will resolve.</param>
        /// <param name="queryManager">The fabric query manager.</param>
        /// <param name="logger">The logger.</param>
        public FabricServiceSiloResolver(
            Uri serviceName,
            IFabricQueryManager queryManager,
            ILogger<FabricServiceSiloResolver> logger)
        {
            this.serviceName = serviceName;
            this.queryManager = queryManager;
            this.log = logger;
            this.partitionChangeHandler = this.OnPartitionChange;
        }

        /// <inheritdoc />
        public void Subscribe(IFabricServiceStatusListener handler)
        {
            this.subscribers.TryAdd(handler, handler);
        }

        /// <inheritdoc />
        public void Unsubscribe(IFabricServiceStatusListener handler)
        {
            this.subscribers.TryRemove(handler, out handler);
        }

        /// <inheritdoc />
        public async Task Refresh()
        {
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug($"Refreshing silos for service {this.serviceName}");
            var result = await this.queryManager.ResolveSilos(this.serviceName);
            lock (this.updateLock)
            {
                this.silos = result;

                // Register for update notifications for each partition.
                var oldRegistrations = this.changeHandlerRegistrations;
                var updatedRegistrations = new Dictionary<Guid, long>();
                foreach (var partition in this.silos)
                {
                    var partitionInfo = partition.Partition;
                    if (oldRegistrations != null && oldRegistrations.ContainsKey(partitionInfo.Id))
                    {
                        updatedRegistrations[partitionInfo.Id] = oldRegistrations[partitionInfo.Id];
                        oldRegistrations.Remove(partitionInfo.Id);
                        if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug($"Partition change handler for partition {partition.Partition} already registered.");
                        continue;
                    }
                    var registrationId = updatedRegistrations[partitionInfo.Id] = this.queryManager.RegisterPartitionChangeHandler(
                        this.serviceName,
                        partitionInfo,
                        this.partitionChangeHandler);
                    if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug($"Registering partition change handler 0x{registrationId:X} for partition {partition.Partition}");
                }

                // Remove old registrations.
                if (oldRegistrations != null)
                {
                    foreach (var registration in oldRegistrations)
                    {
                        if (this.log.IsEnabled(LogLevel.Debug)) this.log.Debug($"Unregistering partition change handler 0x{registration.Value:X}");
                        this.queryManager.UnregisterPartitionChangeHandler(registration.Value);
                    }
                }

                this.changeHandlerRegistrations = updatedRegistrations;
            }

            this.NotifySubscribers();
        }

        /// <summary>
        /// Handler for partition change events.
        /// </summary>
        /// <param name="handlerId">The handler id.</param>
        /// <param name="args">The handler event.</param>
        private void OnPartitionChange(long handlerId, FabricPartitionResolutionChange args)
        {
            if (args.HasException)
            {
                this.log.Warn(
                    (int) ErrorCode.ServiceFabric_Resolver_PartitionResolutionException,
                    "Exception resolving partition change.",
                    args.Exception);
                return;
            }

            var found = false;
            var updated = args.Result;
            lock (this.updateLock)
            {
                for (var i = 0; i < this.silos.Length; i++)
                {
                    var existing = this.silos[i].Partition;
                    if (!existing.IsSamePartitionAs(updated.Partition)) continue;
                    found = true;

                    // Update the partition.
                    this.silos[i] = updated;
                    this.NotifySubscribers();
                    break;
                }
            }

            if (!found)
            {
                var knownPartitions = string.Join(", ", this.silos.Select(s => s.Partition));
                this.log.Warn(
                    (int) ErrorCode.ServiceFabric_Resolver_PartitionNotFound,
                    $"Received update for partition {updated.Partition}, but found no matching partition. Known partitions: {knownPartitions}");
            }
            else if (this.log.IsEnabled(LogLevel.Debug))
            {
                var newSilos = new StringBuilder();
                foreach (var silo in updated.Silos)
                {
                    newSilos.Append($"\n* {silo}");
                }

                this.log.Debug($"Received update for partition {updated.Partition}. Updated values: {newSilos}");
            }
        }

        /// <summary>
        /// Notifies subscribers of updates.
        /// </summary>
        private void NotifySubscribers()
        {
            var copy = this.silos.SelectMany(_ => _.Silos).ToArray();
            foreach (var observer in this.subscribers.Values)
            {
                observer.OnUpdate(copy);
            }
        }
    }
}