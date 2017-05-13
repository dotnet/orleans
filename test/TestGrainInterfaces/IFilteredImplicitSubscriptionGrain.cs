using Orleans;
using Orleans.Streams;
using System;
using System.Threading.Tasks;

namespace UnitTests.GrainInterfaces
{
    public interface IFilteredImplicitSubscriptionGrain : IGrainWithGuidKey
    {
        Task<int> GetCounter(string streamNamespace);
    }

    [Serializable]
    public class RedStreamNamespacePredicate : IStreamNamespacePredicate
    {
        public bool IsMatch(string streamNamespace)
        {
            return streamNamespace.StartsWith("red");
        }
    }
}