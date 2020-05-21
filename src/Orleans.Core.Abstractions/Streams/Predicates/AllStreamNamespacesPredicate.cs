using System;

namespace Orleans.Streams
{
    internal class AllStreamNamespacesPredicate : IStreamNamespacePredicate
    {
        public string PredicatePattern => "*";

        public bool IsMatch(string streamNamespace)
        {
            return true;
        }
    }
}