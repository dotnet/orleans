using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Pooled queue cache stores data in tightly packed structures that need to be transformed to various
    ///   other formats quickly.  Since the data formats may change by queue type and data format,
    ///   this interface allows adapter developers to build custom data transforms appropriate for 
    ///   the various types of queue data.
    /// </summary>
    public interface ICacheDataAdapter
    {
        /// <summary>
        /// Converts a cached message to a batch container for delivery
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <returns>The batch container.</returns>
        IBatchContainer GetBatchContainer(ref CachedMessage cachedMessage);

        /// <summary>
        /// Gets the stream sequence token from a cached message.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <returns>The sequence token.</returns>
        StreamSequenceToken GetSequenceToken(ref CachedMessage cachedMessage);

        /// <summary>
        /// Compares a cached message with a stream sequence token.
        /// </summary>
        /// <param name="cachedMessage">The cached message.</param>
        /// <param name="token">The sequence token.</param>
        /// <returns>A value indicating the relative order of the cached message and token.</returns>
        /// <remarks>
        /// The default implementation uses the allocation-free sequence number and event index fields.
        /// Providers with external offsets can override this method and compare encoded offset data directly.
        /// </remarks>
        int Compare(ref CachedMessage cachedMessage, StreamSequenceToken token)
            => cachedMessage.Compare(token);
    }
}
