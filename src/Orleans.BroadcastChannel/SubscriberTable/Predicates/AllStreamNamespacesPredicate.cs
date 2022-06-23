namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// A stream namespace predicate which matches all namespaces.
    /// </summary>
    internal class AllStreamNamespacesPredicate : IChannelNamespacePredicate
    {
        /// <inheritdoc/>
        public string PredicatePattern => "*";

        /// <inheritdoc/>
        public bool IsMatch(string streamNamespace)
        {
            return true;
        }
    }
}