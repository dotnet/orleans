using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ICounterGrain : IGrainWithGuidKey
    {
        Task IncrementValue();

        Task<int> GetValue();

        Task ResetValue();

        Task<string> GetRuntimeInstanceId();
    }
}