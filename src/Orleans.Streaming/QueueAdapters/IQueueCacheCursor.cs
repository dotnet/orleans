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
        IBatchContainer GetCurrent(out Exception exception);

        /// <summary>
        /// Move to next message in the stream.
        /// If it returns false, there are no more messages.  The enumerator is still
        ///  valid however and can be called again when more data has come in on this
        ///  stream.
        /// </summary>
        /// <returns><see langword="true"/> if there are more items, <see langword="false"/> otherwise</returns>
        bool MoveNext();

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
