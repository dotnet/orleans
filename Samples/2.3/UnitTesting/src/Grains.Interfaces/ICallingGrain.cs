using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ICallingGrain : IGrainWithGuidKey
    {
        Task IncrementAsync();
        Task PublishAsync();
    }
}