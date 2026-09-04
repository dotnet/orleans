using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common;

/// <summary>
/// Creates independent readers for retained records in a recoverable stream partition.
/// </summary>
/// <typeparam name="TQueueMessage">The immutable source record type.</typeparam>
public interface IRecoverableStreamReplaySourceFactory<TQueueMessage>
{
    /// <summary>
    /// Creates a reader positioned at an inclusive provider token.
    /// </summary>
    /// <param name="streamId">The stream requested by the replay cursor.</param>
    /// <param name="token">The inclusive provider token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An independent historical reader.</returns>
    /// <remarks>
    /// The factory validates the provider, partition, token type, and retained lower bound before
    /// admitting the reader. Invalid or expired positions surface as <see cref="DataNotAvailableException"/>.
    /// </remarks>
    ValueTask<IRecoverableStreamReplaySource<TQueueMessage>> Create(
        StreamId streamId,
        StreamSequenceToken token,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads retained immutable records without changing the live source offset or durable queue checkpoint.
/// </summary>
/// <typeparam name="TQueueMessage">The immutable source record type.</typeparam>
public interface IRecoverableStreamReplaySource<TQueueMessage> : IAsyncDisposable
{
    /// <summary>
    /// Reads the next ordered page of retained partition records.
    /// </summary>
    /// <param name="maxCount">The maximum number of records to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A page and its provider-tail state.</returns>
    ValueTask<RecoverableStreamReplayReadResult<TQueueMessage>> Read(
        int maxCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Notifies the reader that records were accepted by the replay cache.
    /// </summary>
    /// <param name="messages">The accepted records.</param>
    void MessagesAdded(IReadOnlyList<TQueueMessage> messages) { }

    /// <summary>
    /// Notifies the reader that records were rejected by the replay cache.
    /// </summary>
    /// <param name="messages">The rejected records.</param>
    void MessagesAddFailed(IReadOnlyList<TQueueMessage> messages) { }

    /// <summary>
    /// Advances replay-fragment reclamation progress after all attached cursors have safely passed a position.
    /// </summary>
    /// <param name="token">The contiguous safe partition position.</param>
    void UpdateProgress(StreamSequenceToken token) { }

    /// <summary>
    /// Stops the reader because its owning receiver is shutting down.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <remarks>
    /// Provider-visible replay protection remains active until its lease expires so that queue ownership
    /// can transfer without opening a cleanup window.
    /// </remarks>
    ValueTask ShutdownAsync(CancellationToken cancellationToken)
        => DisposeAsync();
}

/// <summary>
/// Represents one retained-history read.
/// </summary>
/// <typeparam name="TQueueMessage">The immutable source record type.</typeparam>
public readonly struct RecoverableStreamReplayReadResult<TQueueMessage>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RecoverableStreamReplayReadResult{TQueueMessage}"/> struct.
    /// </summary>
    /// <param name="messages">The ordered records.</param>
    /// <param name="isAtTail">Whether the reader reached the provider tail represented by this read.</param>
    public RecoverableStreamReplayReadResult(
        IReadOnlyList<TQueueMessage> messages,
        bool isAtTail)
    {
        Messages = messages ?? throw new ArgumentNullException(nameof(messages));
        IsAtTail = isAtTail;
    }

    /// <summary>
    /// Gets the ordered records.
    /// </summary>
    public IReadOnlyList<TQueueMessage> Messages { get; }

    /// <summary>
    /// Gets a value indicating whether this read reached the provider tail.
    /// </summary>
    public bool IsAtTail { get; }
}
