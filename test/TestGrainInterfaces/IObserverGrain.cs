using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IObserverGrain : IGrainWithIntegerKey
    {
        Task SetTarget(ISimpleObserverableGrain target);
        Task Subscribe(ISimpleGrainObserver observer);
    }
}
