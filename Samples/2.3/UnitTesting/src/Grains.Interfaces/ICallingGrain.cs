using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ICallingGrain : IGrainWithStringKey
    {
        Task IncrementAsync();
        Task PublishAsync();
    }
}