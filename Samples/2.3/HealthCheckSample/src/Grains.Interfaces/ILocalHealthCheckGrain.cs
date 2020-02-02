using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ILocalHealthCheckGrain : IGrainWithGuidKey
    {
        Task PingAsync();
    }
}
