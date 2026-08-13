using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Reads ordered records from a recoverable stream partition.
    /// </summary>
    /// <typeparam name="TQueueMessage">The source record type.</typeparam>
    public interface IRecoverableStreamSource<TQueueMessage>
    {
        /// <summary>
        /// Initializes the source at the requested position.
        /// </summary>
        /// <param name="position">The durable checkpoint or configured start policy.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <remarks>A durable checkpoint always takes precedence and reads must begin strictly after it.</remarks>
        Task Initialize(RecoverableStreamStartPosition position, CancellationToken cancellationToken);

        /// <summary>
        /// Reads an ordered batch of immutable source records.
        /// </summary>
        Task<IReadOnlyList<TQueueMessage>> Read(int maxCount, CancellationToken cancellationToken);

        /// <summary>
        /// Notifies the source that records were successfully admitted to the cache.
        /// </summary>
        /// <param name="messages">The admitted records.</param>
        /// <remarks>Sources should advance volatile read offsets only in this callback.</remarks>
        void MessagesAdded(IReadOnlyList<TQueueMessage> messages) { }

        /// <summary>
        /// Notifies the source that records could not be admitted to the cache.
        /// </summary>
        /// <param name="messages">The records which were not admitted.</param>
        void MessagesAddFailed(IReadOnlyList<TQueueMessage> messages) { }

        /// <summary>
        /// Shuts the partition source down.
        /// </summary>
        Task Shutdown(CancellationToken cancellationToken);
    }
}
