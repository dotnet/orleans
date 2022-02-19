

namespace Orleans.Streams
{
    /// <summary>
    /// Functionality for controlling the flow of retrieved queue items.
    /// </summary>
    public interface IQueueFlowController
    {
        /// <summary>
        /// Gets the maximum number of items that can be added.
        /// </summary>
        /// <returns>
        /// The maximum number of items that can be added.
        /// </returns>
        int GetMaxAddCount();
    }
}
