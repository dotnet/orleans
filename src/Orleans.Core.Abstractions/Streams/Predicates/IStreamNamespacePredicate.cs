namespace Orleans.Streams
{
    /// <summary>
    /// Stream namespace predicate used for filtering implicit subscriptions using 
    /// <see cref="ImplicitStreamSubscriptionAttribute"/>.
    /// </summary>
    /// <remarks>All implementations must be serializable.</remarks>
    public interface IStreamNamespacePredicate
    {
        /// <summary>
        /// Defines if the consumer grain should subscribe to the specified namespace.
        /// </summary>
        /// <param name="streamNamespace">The target stream namespace to check.</param>
        /// <returns><c>true</c>, if the grain should subscribe to the specified namespace; <c>false</c>, otherwise.
        /// </returns>
        bool IsMatch(string streamNamespace);
    }
}