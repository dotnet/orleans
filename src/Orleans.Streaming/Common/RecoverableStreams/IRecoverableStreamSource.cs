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
        /// Shuts the partition source down.
        /// </summary>
        Task Shutdown(CancellationToken cancellationToken);
    }
}
