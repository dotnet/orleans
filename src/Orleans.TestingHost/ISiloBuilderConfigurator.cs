using System;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows implementations to configure the host builder when starting up each silo in the test cluster.
    /// </summary>
    [Obsolete("Implement " + nameof(ISiloConfigurator) + " and " + nameof(IHostConfigurator) + " instead.")]
    public interface ISiloBuilderConfigurator
    {
        /// <summary>
        /// Configures the silo host builder.
        /// </summary>
        void Configure(ISiloHostBuilder hostBuilder);
    }
}