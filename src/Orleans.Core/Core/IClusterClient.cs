using System;

namespace Orleans
{
    /// <summary>
    /// Client interface for interacting with an Orleans cluster.
    /// </summary>
    public interface IClusterClient : IGrainFactory
    {
        /// <summary>
        /// Gets the service provider used by this client.
        /// </summary>
        IServiceProvider ServiceProvider { get; }
    }
}