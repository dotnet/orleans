using System;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Stream namespace predicate which matches exactly one, specified
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    internal sealed class ExactMatchChannelNamespacePredicate : IChannelNamespacePredicate
    {
        internal const string Prefix = "namespace:";

        [Id(0)]
        private readonly string targetStreamNamespace;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExactMatchChannelNamespacePredicate"/> class.
        /// </summary>
        /// <param name="targetStreamNamespace">The target stream namespace.</param>
        public ExactMatchChannelNamespacePredicate(string targetStreamNamespace)
        {
            this.targetStreamNamespace = targetStreamNamespace;
        }

        /// <inheritdoc/>
        public string PredicatePattern => $"{Prefix}{this.targetStreamNamespace}";

        /// <inheritdoc/>
        public bool IsMatch(string streamNamespace)
        {
            return string.Equals(targetStreamNamespace, streamNamespace?.Trim());
        }
    }
}