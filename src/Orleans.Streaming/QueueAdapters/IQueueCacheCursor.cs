using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Enumerates the messages in a stream.
    /// </summary>
    public interface IQueueCacheCursor : IDisposable
    {
        /// <summary>
        /// Get the current value.
        /// </summary>
        /// <param name="exception">The resulting exception.</param>
        /// <returns>
        /// Returns the current batch container.
        /// If null then the stream has completed or there was a stream error.  
        /// If there was a stream error, an error exception will be provided in the output.
        /// </returns>
        IBatchContainer? GetCurrent(out Exception? exception);

        /// <summary>
        /// Move to next message in the stream.
        /// If it returns false, there are no more messages.  The enumerator is still
        ///  valid however and can be called again when more data has come in on this
        ///  stream.
        /// </summary>
        /// <returns><see langword="true"/> if there are more items, <see langword="false"/> otherwise</returns>
        /// <exception cref="QueueCacheMissException">
        /// The cursor position is older than the messages retained by the cache.
        /// </exception>
        [Obsolete("Use MoveNextWithResult instead.")]
        bool MoveNext();

        /// <summary>
        /// Attempts to move to the next message in the stream.
        /// </summary>
        /// <returns>
        /// A successful result when the cursor has a current item, <see cref="QueueCacheCursorMoveResultKind.NoData"/>
        /// when no item is available, or a cache-miss result containing the unavailable position and cache bounds.
        /// </returns>
        QueueCacheCursorMoveResult MoveNextWithResult()
        {
            try
            {
#pragma warning disable CS0618 // Required for compatibility with cursors which only implement the legacy method.
                return MoveNext() ? QueueCacheCursorMoveResult.Success : QueueCacheCursorMoveResult.NoData;
#pragma warning restore CS0618
            }
            catch (QueueCacheMissException exception)
            {
                return QueueCacheCursorMoveResult.FromCacheMiss(
                    new(exception.Requested, exception.Low, exception.High));
            }
        }

        /// <summary>
        /// Refreshes the cache cursor. Called when new data is added into a cache.
        /// </summary>
        /// <param name="token">The token.</param>
        void Refresh(StreamSequenceToken token);

        /// <summary>
        /// Records that delivery of the current event has failed
        /// </summary>
        void RecordDeliveryFailure();
    }
}
