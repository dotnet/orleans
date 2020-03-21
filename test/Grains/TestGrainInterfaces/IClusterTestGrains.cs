using System.Threading.Tasks;
using Orleans;

namespace TestGrainInterfaces
{
    public interface IClusterTestGrain : IGrainWithIntegerKey
    {
        Task<int> SayHelloAsync();
        Task Deactivate();
        Task<string> GetRuntimeId();
        Task Subscribe(IClusterTestListener listener);
        Task EnableStreamNotifications();
    }

    public interface IClusterTestListener : IGrainObserver
    {
        void GotHello(int number);
    }
}
