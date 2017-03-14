using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Runtime;
using Microsoft.Orleans.ServiceFabric.Models;
using Microsoft.Orleans.ServiceFabric.Utilities;

namespace Microsoft.Orleans.ServiceFabric
{
    /// <summary>
    /// Provides information about an Orleans cluster hosted on Service Fabric.
    /// </summary>
    internal class FabricServiceSiloResolver : IFabricServiceSiloResolver
    {
        private readonly Uri serviceName;
        private readonly IFabricQueryManager queryManager;
        private readonly Logger log;
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
        /// <param name="loggerFactory">The logger factory.</param>
        public FabricServiceSiloResolver(
            Uri serviceName,
            IFabricQueryManager queryManager,
            Func<string, Logger> loggerFactory = null)
        {
            this.serviceName = serviceName;
            this.queryManager = queryManager;
            this.log = loggerFactory?.Invoke(nameof(FabricServiceSiloResolver));
            this.partitionChangeHandler = this.OnPartitionChange;
        }
        
        /// <summary>
        /// Subscribes the provided handler for update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        public void Subscribe(IFabricServiceStatusListener handler)
        {
            this.subscribers.TryAdd(handler, handler);
        }

        /// <summary>
        /// Unsubscribes the provided handler from update notifications.
        /// </summary>
        /// <param name="handler">The update notification handler.</param>
        public void Unsubscribe(IFabricServiceStatusListener handler)
        {
            this.subscribers.TryRemove(handler, out handler);
        }

        /// <summary>
        /// Forces a refresh of the partitions.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public async Task Refresh()
        {
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
                    updatedRegistrations[partitionInfo.Id] = this.queryManager.RegisterPartitionChangeHandler(
                        this.serviceName,
                        partitionInfo,
                        this.partitionChangeHandler);
                }

                // Remove old registrations.
                if (oldRegistrations != null)
                {
                    foreach (var registration in oldRegistrations)
                    {
                        if (!updatedRegistrations.ContainsKey(registration.Key))
                        {
                            this.queryManager.UnregisterPartitionChangeHandler(registration.Value);
                        }
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
                this.log?.Warn(
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

                    if (updated.Partition.IsOlderThan(existing))
                    {
                        this.log?.Info($"Update for partition {updated} is superseded by existing version.");

                        // Do not update the partition if the exiting one has a newer version than the update.
                        break;
                    }

                    // Update the partition.
                    this.silos[i] = updated;
                    this.NotifySubscribers();
                    break;
                }
            }

            if (!found)
            {
                var knownPartitions = string.Join(", ", this.silos.Select(s => s.Partition));
                this.log?.Warn(
                    (int)ErrorCode.ServiceFabric_Resolver_PartitionNotFound,
                    $"Received update for partition {updated.Partition}, but found no matching partition. Known partitions: {knownPartitions}");
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