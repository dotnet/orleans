using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams;

/// <summary>
/// Represents a temporary retained-history read failure which can be retried from the last safe token.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class TransientStreamReplayException : OrleansException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TransientStreamReplayException"/> class.
    /// </summary>
    public TransientStreamReplayException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientStreamReplayException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public TransientStreamReplayException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientStreamReplayException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The provider failure.</param>
    public TransientStreamReplayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    [Obsolete]
    private TransientStreamReplayException(
        SerializationInfo info,
        StreamingContext context)
        : base(info, context)
    {
    }
}

/// <summary>
/// Describes the result of asynchronously advancing a queue cache cursor.
/// </summary>
public enum QueueCacheCursorMoveNextResult
{
    /// <summary>
    /// The cursor advanced to an item which is available from <see cref="IQueueCacheCursor.GetCurrent(out System.Exception?)"/>.
    /// </summary>
    ItemAvailable,

    /// <summary>
    /// The historical reader reached a temporary provider tail and can be called again.
    /// </summary>
    TemporaryTail,

    /// <summary>
    /// The historical phase completed and the cursor is attached to ordinary live delivery.
    /// The caller continues with <see cref="IQueueCacheCursor.MoveNext"/> or calls
    /// <see cref="IAsyncQueueCacheCursor.MoveNextAsync"/> again.
    /// </summary>
    Completed,
}

/// <summary>
/// Extends a queue cache cursor with an asynchronous path for loading retained records.
/// </summary>
/// <remarks>
/// Calls to <see cref="MoveNextAsync"/> are non-reentrant. Cancellation stops the current wait without
/// invalidating the cursor. Disposing the cursor cancels pending work and releases its historical reader.
/// </remarks>
public interface IAsyncQueueCacheCursor : IQueueCacheCursor
{
    /// <summary>
    /// Advances the cursor, awaiting retained provider data when it is outside the live cache.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for this call.</param>
    /// <returns>The cursor transition result.</returns>
    ValueTask<QueueCacheCursorMoveNextResult> MoveNextAsync(CancellationToken cancellationToken);
}
