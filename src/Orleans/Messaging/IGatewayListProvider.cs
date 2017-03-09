using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    public interface IGatewayListProvider
    {
        /// <summary>
        /// Initializes the provider, will be called before all other methods
        /// </summary>
        /// <param name="clientConfiguration">the given client configuration</param>
        /// <param name="logger">the logger to be used by the provider</param>
        Task InitializeGatewayListProvider(ClientConfiguration clientConfiguration, Logger logger);
        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        Task<IList<Uri>> GetGateways();

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
        /// </summary>
        TimeSpan MaxStaleness { get; }

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gw list.
        /// (currently only the static config based StaticGatewayListProvider is not updatable. All others are.)
        /// </summary>
        bool IsUpdatable { get; }
    }
}
