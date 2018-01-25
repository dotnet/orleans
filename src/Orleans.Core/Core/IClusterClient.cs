using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Client interface for interacting with an Orleans cluster.
    /// </summary>
    public interface IClusterClient : IDisposable, IGrainFactory
    {
        /// <summary>
        /// Gets a value indicating whether or not this client is initialized.
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Gets the service provider used by this client.
        /// </summary>
        IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Gets the client configuration.
        /// </summary>
        ClientConfiguration Configuration { get; }

        /// <summary>
        /// Returns the <see cref="IStreamProvider"/> with the specified <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The name of the stream provider.</param>
        /// <returns>The <see cref="IStreamProvider"/> with the specified <paramref name="name"/>.</returns>
        IStreamProvider GetStreamProvider(string name);

        /// <summary>
        /// Starts the client and connects to the configured cluster.
        /// </summary>
        /// <remarks>This method may be called at-most-once per instance.</remarks>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Connect();

        /// <summary>
        /// Stops the client gracefully, disconnecting from the cluster.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Close();

        /// <summary>
        /// Aborts the client ungracefully.
        /// </summary>
        void Abort();
    }
}