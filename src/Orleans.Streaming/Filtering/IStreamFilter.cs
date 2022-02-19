using Orleans.Runtime;

namespace Orleans.Streams.Filtering
{
    /// <summary>
    /// Functionality for filtering streams.
    /// </summary>
    public interface IStreamFilter
    {
        /// <summary>
        /// Returns a value indicating if the specified stream item should be delivered.
        /// </summary>
        /// <param name="streamId">The stream identifier.</param>
        /// <param name="item">The stream item.</param>
        /// <param name="filterData">The filter data.</param>
        /// <returns><see langword="true" /> if the stream item should be delivered, <see langword="false" /> otherwise.</returns>
        bool ShouldDeliver(StreamId streamId, object item, string filterData);
    }

    internal sealed class NoOpStreamFilter : IStreamFilter
    {
        public bool ShouldDeliver(StreamId streamId, object item, string filterData) => true;
    }
}
