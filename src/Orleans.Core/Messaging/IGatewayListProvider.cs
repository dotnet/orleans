using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    /// <remarks>
    /// Providers implement the cancellation-aware Async methods to cancel their underlying operations.
    /// The default implementations support existing providers by cancelling the caller's wait and
    /// observing completion of the tokenless operation.
    /// </remarks>
    public interface IGatewayListProvider
    {
        /// <summary>
        /// Initializes the provider, will be called before all other methods.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task InitializeGatewayListProvider();

        /// <summary>
        /// Initializes the provider before gateway discovery begins.
        /// </summary>
        /// <param name="cancellationToken">The token used to cancel initialization.</param>
        /// <returns>A task representing the initialization operation.</returns>
        async Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = InitializeGatewayListProvider();
            operation.Ignore();
            await operation.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        /// <returns>The list of gateway endpoints.</returns>
        Task<IList<Uri>> GetGateways();

        /// <summary>
        /// Gets the gateway endpoints which clients can use to connect to the cluster.
        /// </summary>
        /// <param name="cancellationToken">The token used to cancel gateway discovery.</param>
        /// <returns>The list of gateway endpoints.</returns>
        async Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operation = GetGateways();
            operation.Ignore();
            return await operation.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// Gets the period of time between refreshes.
        /// </summary>
        TimeSpan MaxStaleness { get; }

        /// <summary>
        /// Gets a value indicating whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gateway list.
        /// </summary>
        [Obsolete("This attribute is no longer used and all providers are considered updatable")]
        bool IsUpdatable { get; }
    }
}
