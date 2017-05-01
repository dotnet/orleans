using System;

namespace Orleans.Streams
{
    [Serializable]
    internal class ExactMatchStreamNamespacePredicate : IStreamNamespacePredicate
    {
        private readonly string targetStreamNamespace;

        public ExactMatchStreamNamespacePredicate(string targetStreamNamespace)
        {
            this.targetStreamNamespace = targetStreamNamespace;
        }

        public bool IsMatch(string streamNamespace)
        {
            return string.Equals(targetStreamNamespace, streamNamespace?.Trim());
        }
    }
}