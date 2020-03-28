using System.Threading.Tasks;
using Orleans.Runtime;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestClientObject : IAddressable
    {
        Task<string> OnHappyPath(string message);
        Task OnSadPath(string message);
        Task<int> OnSerialStress(int n);
        Task<int> OnParallelStress(int n);
    }
}
