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

        /// <summary>
        /// Gets a pattern to describe this predicate. This value is passed to instances of <see cref="IStreamNamespacePredicateProvider"/> to recreate this predicate.
        /// </summary>
        string PredicatePattern { get; }
    }

    /// <summary>
    /// Converts predicate pattern strings to <see cref="IStreamNamespacePredicate"/> instances.
    /// </summary>
    /// <seealso cref="IStreamNamespacePredicate.PredicatePattern"/>
    public interface IStreamNamespacePredicateProvider
    {
        /// <summary>
        /// Get the predicate matching the provided pattern. Returns <see langword="false"/> if this provider cannot match the predicate.
        /// </summary>
        bool TryGetPredicate(string predicatePattern, out IStreamNamespacePredicate predicate);
    }
}