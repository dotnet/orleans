using System;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// Client interface for interacting with an Orleans cluster.
    /// </summary>
    public interface IClusterClient : IGrainFactory, IAsyncDisposable, IDisposable
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
        /// Starts the client and connects to the configured cluster.
        /// </summary>
        /// <remarks>This method may be called at-most-once per instance.</remarks>
        /// <param name="retryFilter">
        /// An optional delegate which determines whether or not the initial connection attempt should be retried.
        /// </param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Connect(Func<Exception, Task<bool>> retryFilter = null);

        /// <summary>
        /// Stops the client gracefully, disconnecting from the cluster.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Close();

        /// <summary>
        /// Aborts the client ungracefully.
        /// </summary>
        Task AbortAsync();
    }
}