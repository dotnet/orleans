using System.Threading.Tasks;
using Orleans;

namespace BenchmarkGrainInterfaces.Ping
{
    public interface IPingGrain : IGrainWithIntegerKey
    {
        Task Run();
    }
}
