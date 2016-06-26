using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IStreamInterceptionGrain : IGrainWithGuidKey
    {
        Task<int> GetLastStreamValue();
    }
}