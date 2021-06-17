using System;

namespace Orleans.Streams
{
    [Serializable]
    [GenerateSerializer]
    internal class ExactMatchStreamNamespacePredicate : IStreamNamespacePredicate
    {
        internal const string Prefix = "namespace:";
        [Id(1)]
        private readonly string targetStreamNamespace;

        public ExactMatchStreamNamespacePredicate(string targetStreamNamespace)
        {
            this.targetStreamNamespace = targetStreamNamespace;
        }

        public string PredicatePattern => $"{Prefix}{this.targetStreamNamespace}";

        public bool IsMatch(string streamNamespace)
        {
            return string.Equals(targetStreamNamespace, streamNamespace?.Trim());
        }
    }
}