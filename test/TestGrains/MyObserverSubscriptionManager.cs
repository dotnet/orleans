using System;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public class MyObserverSubscriptionManager<T> : ObserverSubscriptionManager<T> where T : IGrainObserver
    {
        public int Foo { get; set; }
    }

    [Serializable]
    public class MyState<T>
         where T : IGrainObserver
    {
        public MyObserverSubscriptionManager<T> Subscription { get; set; }
    }

    public class MyGrain<T> : Grain<MyState<T>>, ISimpleGrain
        where T : IGrainObserver
    {
        public Task SetA(int a)
        {
            throw new NotImplementedException();
        }

        public Task SetB(int b)
        {
            throw new NotImplementedException();
        }

        public Task IncrementA()
        {
            throw new NotImplementedException();
        }

        public Task<int> GetAxB()
        {
            throw new NotImplementedException();
        }

        public Task<int> GetAxB(int a, int b)
        {
            throw new NotImplementedException();
        }

        public Task<int> GetA()
        {
            throw new NotImplementedException();
        }
    }
}
