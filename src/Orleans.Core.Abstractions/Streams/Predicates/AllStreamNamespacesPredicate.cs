using System;

namespace Orleans.Streams
{
    [Serializable]
    internal class AllStreamNamespacesPredicate : IStreamNamespacePredicate
    {
        public bool IsMatch(string streamNamespace)
        {
            return true;
        }
    }
}