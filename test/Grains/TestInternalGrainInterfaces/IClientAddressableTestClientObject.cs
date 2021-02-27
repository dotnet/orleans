using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IClientAddressableTestClientObject : IGrainObserver
    {
        Task<string> OnHappyPath(string message);
        Task OnSadPath(string message);
        Task<int> OnSerialStress(int n);
        Task<int> OnParallelStress(int n);
    }
}
