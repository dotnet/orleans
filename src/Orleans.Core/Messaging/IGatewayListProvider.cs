using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    public interface IGatewayListProvider
    {
        /// <summary>
        /// Initializes the provider, will be called before all other methods.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task InitializeGatewayListProvider();

        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        /// <returns>The list of gateway endpoints.</returns>
        Task<IList<Uri>> GetGateways();

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
