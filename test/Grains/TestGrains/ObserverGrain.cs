using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ObserverGrain : Grain, IObserverGrain, ISimpleGrainObserver
    {
        protected  ISimpleGrainObserver Observer { get; set; } // supports only a single observer

        protected ISimpleObserverableGrain Target { get; set; }

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

        public void StateChanged(int a, int b)
        {
            Observer.StateChanged(a, b);
        }
    }
}
