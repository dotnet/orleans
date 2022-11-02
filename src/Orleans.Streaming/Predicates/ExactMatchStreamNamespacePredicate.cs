using System;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream namespace predicate which matches exactly one, specified
    /// </summary>
    [Serializable, GenerateSerializer, Immutable]
    internal sealed class ExactMatchStreamNamespacePredicate : IStreamNamespacePredicate
    {
        internal const string Prefix = "namespace:";

        [Id(0)]
        private readonly string targetStreamNamespace;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExactMatchStreamNamespacePredicate"/> class.
        /// </summary>
        /// <param name="targetStreamNamespace">The target stream namespace.</param>
        public ExactMatchStreamNamespacePredicate(string targetStreamNamespace)
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