using System;
using Orleans.Streams;

namespace UnitTests.Grains
{
    [Serializable]
    public class RedStreamNamespacePredicate : IStreamNamespacePredicate
    {
        public bool IsMatch(string streamNamespace)
        {
            return streamNamespace.StartsWith("red");
        }
    }
}