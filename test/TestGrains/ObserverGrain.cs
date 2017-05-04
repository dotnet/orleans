using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ObserverGrain : Grain, IObserverGrain, ISimpleGrainObserver
    {
        protected  ISimpleGrainObserver Observer { get; set; } // supports only a single observer

        protected ISimpleObserverableGrain Target { get; set; }

        #region IObserverGrain Members

        public Task SetTarget(ISimpleObserverableGrain target)
        {
            Target = target;
            return target.Subscribe(this);
        }

        public Task Subscribe(ISimpleGrainObserver observer)
        {
            this.Observer = observer;
            return Task.CompletedTask;
        }

        #endregion

        #region ISimpleGrainObserver Members

        public void StateChanged(int a, int b)
        {
            Observer.StateChanged(a, b);
        }

        #endregion
    }
}
