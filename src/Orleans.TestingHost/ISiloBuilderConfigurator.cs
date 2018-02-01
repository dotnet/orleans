using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows implementations to configure the host builder when starting up each silo in the test cluster.
    /// </summary>
    public interface ISiloBuilderConfigurator
    {
        /// <summary>
        /// Configures the host builder.
        /// </summary>
        void Configure(ISiloHostBuilder hostBuilder);
    }
}