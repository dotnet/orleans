using System.Threading.Tasks;
using Orleans;

namespace Grains
{
    public interface ICallingTimerGrain : IGrainWithStringKey
    {
        Task IncrementAsync();
    }
}