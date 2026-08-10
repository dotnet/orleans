using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Factory for creating <see cref="IStreamQueueCheckpointer{TCheckpoint}"/> instances.
    /// </summary>
    public interface IStreamQueueCheckpointerFactory
    {
        /// <summary>
        /// Creates a stream checkpointer for the specified partition.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <returns>The stream checkpointer.</returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task<IStreamQueueCheckpointer<string>> Create(string partition);

        /// <summary>
        /// Creates a stream checkpointer for the specified partition.
        /// </summary>
        /// <param name="partition">The partition.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The stream checkpointer.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task<IStreamQueueCheckpointer<string>> Create(string partition, CancellationToken cancellationToken)
            => Create(partition);
#pragma warning restore CS0618
    }

    /// <summary>
    /// Functionality for checkpointing a stream.
    /// </summary>
    /// <typeparam name="TCheckpoint">The checkpoint type.</typeparam>
    public interface IStreamQueueCheckpointer<TCheckpoint>
    {
        /// <summary>
        /// Gets a value indicating whether a checkpoint exists.
        /// </summary>
        /// <value><see langword="true" /> if checkpoint exists; otherwise, <see langword="false" />.</value>
        bool CheckpointExists { get; }

        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <returns>The checkpoint.</returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task<TCheckpoint> Load();

        /// <summary>
        /// Loads the checkpoint.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The checkpoint.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task<TCheckpoint> Load(CancellationToken cancellationToken) => Load();
#pragma warning restore CS0618

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="utcNow">The current UTC time.</param>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        void Update(TCheckpoint offset, DateTime utcNow);

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="utcNow">The current UTC time.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        void Update(TCheckpoint offset, DateTime utcNow, CancellationToken cancellationToken)
            => Update(offset, utcNow);
#pragma warning restore CS0618

        /// <summary>
        /// Flushes any pending checkpoint to persistent storage, ensuring the latest offset is durably saved.
        /// Called during shutdown or rebalancing to prevent message replay on restart.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the flush operation.</returns>
        Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
