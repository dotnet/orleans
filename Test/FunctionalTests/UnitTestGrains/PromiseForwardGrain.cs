﻿using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;


namespace UnitTestGrains
{
    public interface ISimpleGrainState : IGrainState
    {
        int A { get; set; }
        int EventDelay { get; set; }
        ObserverSubscriptionManager<ISimpleGrainObserver> Observers { get; set; }
    }

    /// <summary>
    /// A simple grain that allows to set two agruments and then multiply them.
    /// </summary>
    public class PromiseForwardGrain : Grain<ISimpleGrainState>, IPromiseForwardGrain
    {
        protected  ISimpleGrain MySimpleGrain { get; set; }
        protected int b = 0;
        public Task<int> GetAxB_Async()
        {
            return GetSimpleGrain().GetAxB();
        }
        public Task<int> GetAxB_Async(int a, int b)
        {
            return GetSimpleGrain().GetAxB(a, b);
        }
        public Task SetA_Async(int a)
        {
            return GetSimpleGrain().SetA(a);
        }
        public Task SetB_Async(int b)
        {
            return GetSimpleGrain().SetB(b);
        }
        public Task IncrementA_Async()
        {
            return GetSimpleGrain().IncrementA();
        }
        public Task<int> GetA_Async()
        {
            return GetSimpleGrain().GetA();
        }

        public async Task SetA(int a)
        {
            await GetSimpleGrain().SetA(a);
        }
        public async Task SetB(int a)
        {
            await GetSimpleGrain().SetB(a);
        }
        public Task<int> GetAxB()
        {
            return GetSimpleGrain().GetAxB();
        }
        public Task<int> GetAxB(int a, int b)
        {
            return GetSimpleGrain().GetAxB(a, b);
        }
        public async Task IncrementA()
        {
            await GetSimpleGrain().IncrementA();
        }
        public Task<int> GetA()
        {
            return GetSimpleGrain().GetA();
        }
        
        private ISimpleGrain GetSimpleGrain()
        {
            if( MySimpleGrain == null )
                MySimpleGrain = SimpleGrainFactory.GetGrain((new Random()).Next(), UnitTestGrainInterfacesConstants.SimpleGrainNamePrefix);

            return MySimpleGrain;
        }

        public Task Subscribe(ISimpleGrainObserver observer)
        {
            State.Observers.Subscribe(observer);
            return TaskDone.Done;
        }

        public Task Unsubscribe(ISimpleGrainObserver observer)
        {
            State.Observers.Unsubscribe(observer);
            return TaskDone.Done;
        }

        protected void RaiseStateUpdateEvent()
        {
            State.Observers.Notify((ISimpleGrainObserver observer) =>
            {
                observer.StateChanged(State.A, b);
            });
        }

        public Task<int> A
        {
            get { return Task.FromResult(State.A); }
        }
    }
}
