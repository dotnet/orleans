using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Hosting
{
    /// <summary>
    /// Represents a silo instance.
    /// </summary>
    public interface ISiloHost : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Starts this silo.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task StartAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Stops this silo.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        /// <remarks>
        /// A stopped silo cannot be restarted.
        /// If the provided <paramref name="cancellationToken"/> is canceled or becomes canceled during execution, the silo will terminate ungracefully.
        /// </remarks>
        Task StopAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Gets the service provider used by this silo.
        /// </summary>
        IServiceProvider Services { get; }

        /// <summary>
        /// Gets a <see cref="Task"/> which completes when this silo stops.
        /// </summary>
        Task Stopped { get; }
    }
}