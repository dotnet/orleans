using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ITimerGrain : IGrainWithStringKey
    {
        Task IncrementAsync();
    }
}