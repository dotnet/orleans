namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Monitor track object pool related metrics
    /// </summary>
    public interface IObjectPoolMonitor
    {
        /// <summary>
        /// Track every time when an object is allocated
        /// </summary>
        void TrackObjectAllocated();

        /// <summary>
        /// Track every time an object was released back to the pool
        /// </summary>
        void TrackObjectReleased();

        /// <summary>
        /// Periodically report object pool status
        /// </summary>
        /// <param name="totalObjects">Total size of object pool</param>
        /// <param name="availableObjects">Count for objects in the pool which is available for allocating</param>
        /// <param name="claimedObjects">Count for objects which are claimed, hence not available</param>
        void Report(long totalObjects, long availableObjects, long claimedObjects);
    }
}
