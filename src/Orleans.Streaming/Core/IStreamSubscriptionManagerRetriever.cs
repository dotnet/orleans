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
        /// <returns>The <see cref="IStreamSubscriptionManager"/>, or <see langword="null"/> if the stream provider has no configured subscription manager (e.g. an implicit-subscription-only provider).</returns>
        IStreamSubscriptionManager? GetStreamSubscriptionManager();
    }
}
