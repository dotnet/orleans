using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Client;
using Orleans.Clustering.ServiceFabric.Models;

namespace Orleans.Clustering.ServiceFabric.Utilities
{
    internal interface IFabricQueryManager
    {
        /// <summary>
        /// Returns the silos in the given service.
        /// </summary>
        /// <param name="serviceName">The service name.</param>
        /// <returns></returns>
        Task<ServicePartitionSilos[]> ResolveSilos(Uri serviceName);

        /// <summary>
        /// Registers a partition change handler for the provided partition.
        /// </summary>
        /// <param name="serviceName">The service name.</param>
        /// <param name="servicePartition">The partition.</param>
        /// <param name="handler">The change handler.</param>
        /// <returns>The identifier for the registered handler.</returns>
        long RegisterPartitionChangeHandler(
            Uri serviceName,
            IResolvedServicePartition servicePartition,
            FabricPartitionResolutionChangeHandler handler);

        /// <summary>
        /// Unregisters a partition change handler.
        /// </summary>
        /// <param name="id">The handler id returned from <see cref="RegisterPartitionChangeHandler"/>.</param>
        void UnregisterPartitionChangeHandler(long id);

        /// <summary>
        /// Resolves a partition of the specified service with specified back-off/retry settings on retry-able errors.
        /// </summary>
        /// <param name="serviceName">Uri of the service to resolve</param>
        /// <param name="partitionKey">Key that identifies the partition to resolve</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>
        /// The resolved service partition.
        /// </returns>
        Task<ServicePartitionSilos> ResolvePartition(
            Uri serviceName,
            ServicePartitionKey partitionKey,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Handles a change in partition resolution.
    /// </summary>
    /// <param name="handlerId">The handler id.</param>
    /// <param name="args">The resolution change.</param>
    internal delegate void FabricPartitionResolutionChangeHandler(long handlerId, FabricPartitionResolutionChange args);

    internal class FabricPartitionResolutionChange
    {
        private readonly ServicePartitionSilos result;

        public FabricPartitionResolutionChange(ServicePartitionSilos result, Exception exception)
        {
            this.result = result;
            this.Exception = exception;
        }

        public Exception Exception { get; }

        public ServicePartitionSilos Result
        {
            get
            {
                if (this.Exception != null)
                    throw this.Exception;
                return this.result;
            }
        }

        public bool HasException => this.Exception != null;
    }
}