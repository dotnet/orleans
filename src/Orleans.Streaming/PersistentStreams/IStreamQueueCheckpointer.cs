using System;
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
        Task<IStreamQueueCheckpointer<string>> Create(string partition);
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
        Task<TCheckpoint> Load();

        /// <summary>
        /// Updates the checkpoint.
        /// </summary>
        /// <param name="offset">The offset.</param>
        /// <param name="utcNow">The current UTC time.</param>
        void Update(TCheckpoint offset, DateTime utcNow);
    }
}
