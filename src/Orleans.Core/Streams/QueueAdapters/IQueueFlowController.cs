

namespace Orleans.Streams
{
    public interface IQueueFlowController
    {
        /// <summary>
        /// The limit of the maximum number of items that can be added
        /// </summary>
        int GetMaxAddCount();
    }
}
