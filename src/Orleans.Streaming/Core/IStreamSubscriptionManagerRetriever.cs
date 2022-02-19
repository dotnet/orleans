namespace Orleans.Streams.Core
{
    /// <summary>
    /// Provides functionality for retrieving an <see cref="IStreamSubscriptionManager"/> instance.
    /// </summary>
    public interface IStreamSubscriptionManagerRetriever
    {
        /// <summary>
        /// Gets the stream subscription manager.
        /// </summary>
        /// <returns>The <see cref="IStreamSubscriptionManager"/>.</returns>
        IStreamSubscriptionManager GetStreamSubscriptionManager();
    }
}
