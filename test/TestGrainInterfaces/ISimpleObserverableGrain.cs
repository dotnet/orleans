using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface ISimpleObserverableGrain : ISimpleGrain
    {
        Task Subscribe(ISimpleGrainObserver observer);
        Task Unsubscribe(ISimpleGrainObserver observer);
    }

    public interface ISimpleGrainObserver : IGrainObserver
    {
        void StateChanged(int a, int b);
    }
}
