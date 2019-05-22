using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ICounterGrain : IGrainWithStringKey
    {
        Task IncrementAsync();

        Task<int> GetValueAsync();

        Task SaveAsync();

        Task PublishAsync();
    }
}