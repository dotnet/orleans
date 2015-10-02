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
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    [Serializable]
    public class MyObserverSubscriptionManager<T> : ObserverSubscriptionManager<T> where T : IGrainObserver
    {
        public int Foo { get; set; }
    }

    public class MyState<T> : GrainState
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