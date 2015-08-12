/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    public class SimpleObserverableGrain : Grain, ISimpleObserverableGrain
    {
        private Logger logger;
        internal int A { get; set; }
        internal int B { get; set; }
        internal int EventDelay { get; set; }
        internal ObserverSubscriptionManager<ISimpleGrainObserver> Observers { get; set; }

        public override Task OnActivateAsync()
        {
            EventDelay = 1000;
            Observers = new ObserverSubscriptionManager<ISimpleGrainObserver>();
            logger = GetLogger(String.Format("{0}-{1}-{2}", typeof(SimpleObserverableGrain).Name, base.IdentityString, base.RuntimeIdentity));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

        public async Task SetA(int a)
        {
            logger.Info("SetA={0}", a);
            A = a;

            //If this were run with Task.Run there were no need for the added Unwrap call.
            //However, Task.Run runs in ThreadPool and not in Orleans TaskScheduler, unlike Task.Factory.StartNew.
            //See more at http://dotnet.github.io/orleans/Advanced-Concepts/External-Tasks-and-Grains.
            //The extra task comes from the internal asynchronous lambda due to Task.Delay. For deeper
            //insight, see at http://blogs.msdn.com/b/pfxteam/archive/2012/02/08/10265476.aspx.
            await Task.Factory.StartNew(async () =>
            {
                await Task.Delay(EventDelay);
                RaiseStateUpdateEvent();
            }).Unwrap();            
        }

        public async Task SetB(int b)
        {
            this.B = b;

            //If this were run with Task.Run there were no need for the added Unwrap call.
            //However, Task.Run runs in ThreadPool and not in Orleans TaskScheduler, unlike Task.Factory.StartNew.
            //See more at http://dotnet.github.io/orleans/Advanced-Concepts/External-Tasks-and-Grains.
            //The extra task comes from the internal asynchronous lambda due to Task.Delay. For deeper
            //insight, see at http://blogs.msdn.com/b/pfxteam/archive/2012/02/08/10265476.aspx.
            await Task.Factory.StartNew(async () =>
            {
                await Task.Delay(EventDelay);
                RaiseStateUpdateEvent();
            }).Unwrap();            
        }

        public async Task IncrementA()
        {
            await SetA(A + 1);            
        }

        public Task<int> GetAxB()
        {
            return Task.FromResult(A * B);
        }

        public Task<int> GetAxB(int a, int b)
        {
            return Task.FromResult(a * b);
        }

        public Task<int> GetA()
        {
            return Task.FromResult(A);
        }

        public Task Subscribe(ISimpleGrainObserver observer)
        {
            Observers.Subscribe(observer);
            return TaskDone.Done;
        }

        public Task Unsubscribe(ISimpleGrainObserver observer)
        {
            Observers.Unsubscribe(observer);
            return TaskDone.Done;
        }

        protected void RaiseStateUpdateEvent()
        {
            Observers.Notify((ISimpleGrainObserver observer) =>
            {
                observer.StateChanged(A, B);
            });
        }
    }
}