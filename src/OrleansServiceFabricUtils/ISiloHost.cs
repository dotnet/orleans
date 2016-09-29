using System;
using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    /// <summary>
    /// Abstraction for silo hosts.
    /// </summary>
    internal interface ISiloHost : IDisposable
    {
        /// <summary>
        /// Get the silo's node configuration.
        /// </summary>
        NodeConfiguration NodeConfig { get; }

        /// <summary>
        /// Starts the silo.
        /// </summary>
        /// <param name="siloName">The silo name.</param>
        /// <param name="configuration">The silo configuration.</param>
        void Start(string siloName, ClusterConfiguration configuration);

        /// <summary>
        /// Stops the silo.
        /// </summary>
        void Stop();
    }
}