namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Stream namespace predicate used for filtering implicit subscriptions using 
    /// <see cref="ImplicitChannelSubscriptionAttribute"/>.
    /// </summary>
    /// <remarks>All implementations must be serializable.</remarks>
    public interface IChannelNamespacePredicate
    {
        /// <summary>
        /// Defines if the consumer grain should subscribe to the specified namespace.
        /// </summary>
        /// <param name="streamNamespace">The target stream namespace to check.</param>
        /// <returns><c>true</c>, if the grain should subscribe to the specified namespace; <c>false</c>, otherwise.
        /// </returns>
        bool IsMatch(string streamNamespace);

        /// <summary>
        /// Gets a pattern to describe this predicate. This value is passed to instances of <see cref="IChannelNamespacePredicateProvider"/> to recreate this predicate.
        /// </summary>
        string PredicatePattern { get; }
    }

    /// <summary>
    /// Converts predicate pattern strings to <see cref="IChannelNamespacePredicate"/> instances.
    /// </summary>
    /// <seealso cref="IChannelNamespacePredicate.PredicatePattern"/>
    public interface IChannelNamespacePredicateProvider
    {
        /// <summary>
        /// Get the predicate matching the provided pattern. Returns <see langword="false"/> if this provider cannot match the predicate.
        /// </summary>
        bool TryGetPredicate(string predicatePattern, out IChannelNamespacePredicate predicate);
    }
}