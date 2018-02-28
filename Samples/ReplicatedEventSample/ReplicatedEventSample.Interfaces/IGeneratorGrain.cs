using System.Threading.Tasks;
using Orleans;

namespace ReplicatedEventSample.Interfaces
{
    public interface IGeneratorGrain : IGrainWithIntegerKey
    {
        Task Start();
    }
}