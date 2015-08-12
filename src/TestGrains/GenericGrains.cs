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
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using UnitTests.GrainInterfaces;
using System.Globalization;
using Orleans.CodeGeneration;

namespace UnitTests.Grains
{
    public class SimpleGenericGrainState<T> : GrainState
    {
        public T A { get; set; }
        public T B { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class SimpleGenericGrain1<T> : Grain<SimpleGenericGrainState<T>>, ISimpleGenericGrain1<T>
    {
        public Task<T> GetA()
        {
            return Task.FromResult(State.A);
        }

        public Task SetA(T a)
        {
            State.A = a;
            return TaskDone.Done;
        }

        public Task SetB(T b)
        {
            State.B = b;
            return TaskDone.Done;
        }

        public Task<string> GetAxB()
        {
            string retValue = string.Format("{0}x{1}", State.A, State.B);
            return Task.FromResult(retValue);
        }

        public Task<string> GetAxB(T a, T b)
        {
            string retValue = string.Format("{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }
    }

    public class SimpleGenericGrainUState<U> : GrainState
    {
        public U A { get; set; }
        public U B { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class SimpleGenericGrainU<U> : Grain<SimpleGenericGrainUState<U>>, ISimpleGenericGrainU<U>
    {
        public Task<U> GetA()
        {
            return Task.FromResult(State.A);
        }

        public Task SetA(U a)
        {
            State.A = a;
            return TaskDone.Done;
        }

        public Task SetB(U b)
        {
            State.B = b;
            return TaskDone.Done;
        }

        public Task<string> GetAxB()
        {
            string retValue = string.Format("{0}x{1}", State.A, State.B);
            return Task.FromResult(retValue);
        }

        public Task<string> GetAxB(U a, U b)
        {
            string retValue = string.Format("{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }
    }

    public class SimpleGenericGrain2State<T, U> : GrainState
    {
        public T A { get; set; }
        public U B { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class SimpleGenericGrain2<T, U> : Grain<SimpleGenericGrain2State<T, U>>, ISimpleGenericGrain2<T, U>
    {
        public Task<T> GetA()
        {
            return Task.FromResult(State.A);
        }

        public Task SetA(T a)
        {
            State.A = a;
            return TaskDone.Done;
        }

        public Task SetB(U b)
        {
            State.B = b;
            return TaskDone.Done;
        }

        public Task<string> GetAxB()
        {
            string retValue = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", State.A, State.B);
            return Task.FromResult(retValue);
        }

        public Task<string> GetAxB(T a, U b)
        {
            string retValue = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }
    }

    public class GenericGrainWithNoProperties<T> : Grain, IGenericGrainWithNoProperties<T>
    {
        public Task<string> GetAxB(T a, T b)
        {
            string retValue = string.Format("{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }
    }
    public class GrainWithNoProperties : Grain, IGrainWithNoProperties
    {
        public Task<string> GetAxB(int a, int b)
        {
            string retValue = string.Format("{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }
    }
    public class IGrainWithListFieldsState : GrainState
    {
        public IList<string> Items { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class GrainWithListFields : Grain<IGrainWithListFieldsState>, IGrainWithListFields
    {
        public override Task OnActivateAsync()
        {
            if (State.Items == null)
                State.Items = new List<string>();
            return base.OnActivateAsync();
        }

        public Task AddItem(string item)
        {
            State.Items.Add(item);
            return TaskDone.Done;
        }

        public Task<IList<string>> GetItems()
        {
            return Task.FromResult((State.Items));
        }
    }

    public class GenericGrainWithListFieldsState<T> : GrainState
    {
        public IList<T> Items { get; set; }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class GenericGrainWithListFields<T> : Grain<GenericGrainWithListFieldsState<T>>, IGenericGrainWithListFields<T>
    {
        public override Task OnActivateAsync()
        {
            if (State.Items == null)
                State.Items = new List<T>();

            return base.OnActivateAsync();
        }

        public Task AddItem(T item)
        {
            State.Items.Add(item);
            return TaskDone.Done;
        }

        public Task<IList<T>> GetItems()
        {
            return Task.FromResult(State.Items);
        }
    }

    public class GenericReaderWriterState<T> : GrainState
    {
        public T Value { get; set; }
    }
    

    public class GenericReader2State<TOne, TTwo> : GrainState
    {
        public TOne Value1 { get; set; }
        public TTwo Value2 { get; set; }
    }
    public class GenericReaderWriterGrain2State<TOne, TTwo> : GrainState
    {
        public TOne Value1 { get; set; }
        public TTwo Value2 { get; set; }
    }

    public class GenericReader3State<TOne, TTwo, TThree> : GrainState
    {
        public TOne Value1 { get; set; }
        public TTwo Value2 { get; set; }
        public TThree Value3 { get; set; }
    }


    [StorageProvider(ProviderName = "MemoryStore")]
    public class GenericReaderWriterGrain1<T> : Grain<GenericReaderWriterState<T>>, IGenericReaderWriterGrain1<T>
    {
        public Task SetValue(T value)
        {
            State.Value = value;
            return TaskDone.Done;
        }

        public Task<T> GetValue()
        {
            return Task.FromResult(State.Value);
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class GenericReaderWriterGrain2<TOne, TTwo> : Grain<GenericReaderWriterGrain2State<TOne, TTwo>>, IGenericReaderWriterGrain2<TOne, TTwo>
    {
        public Task SetValue1(TOne value)
        {
            State.Value1 = value;
            return TaskDone.Done;
        }
        public Task SetValue2(TTwo value)
        {
            State.Value2 = value;
            return TaskDone.Done;
        }

        public Task<TOne> GetValue1()
        {
            return Task.FromResult(State.Value1);
        }

        public Task<TTwo> GetValue2()
        {
            return Task.FromResult(State.Value2);
        }
    }

    [StorageProvider(ProviderName = "MemoryStore")]
    public class GenericReaderWriterGrain3<TOne, TTwo, TThree> : Grain<GenericReader3State<TOne, TTwo, TThree>>, IGenericReaderWriterGrain3<TOne, TTwo, TThree>
    {
        public Task SetValue1(TOne value)
        {
            State.Value1 = value;
            return TaskDone.Done;
        }
        public Task SetValue2(TTwo value)
        {
            State.Value2 = value;
            return TaskDone.Done;
        }
        public Task SetValue3(TThree value)
        {
            State.Value3 = value;
            return TaskDone.Done;
        }

        public Task<TThree> GetValue3()
        {
            return Task.FromResult(State.Value3);
        }

        public Task<TOne> GetValue1()
        {
            return Task.FromResult(State.Value1);
        }

        public Task<TTwo> GetValue2()
        {
            return Task.FromResult(State.Value2);
        }
    }

    public class GenericSelfManagedGrain<T, U> : Grain, IGenericSelfManagedGrain<T, U>
    {
        private T _a;
        private U _b;

        public Task<T> GetA()
        {
            return Task.FromResult(_a);
        }

        public Task<string> GetAxB()
        {
            string retValue = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", _a, _b);
            return Task.FromResult(retValue);
        }

        public Task<string> GetAxB(T a, U b)
        {
            string retValue = string.Format(CultureInfo.InvariantCulture, "{0}x{1}", a, b);
            return Task.FromResult(retValue);
        }

        public Task SetA(T a)
        {
            this._a = a;
            return TaskDone.Done;
        }

        public Task SetB(U b)
        {
            this._b = b;
            return TaskDone.Done;
        }
    }

    public class HubGrain<TKey, T1, T2> : Grain, IHubGrain<TKey, T1, T2>
    {
        public virtual Task Bar(TKey key, T1 message1, T2 message2)
        {
            throw new System.NotImplementedException();
        }
    }

    public class EchoHubGrain<TKey, TMessage> : HubGrain<TKey, TMessage, TMessage>, IEchoHubGrain<TKey, TMessage>
    {
        private int _x;

        public Task Foo(TKey key, TMessage message, int x)
        {
            _x = x;
            return TaskDone.Done;
        }

        public override Task Bar(TKey key, TMessage message1, TMessage message2)
        {
            return TaskDone.Done;
        }

        public Task<int> GetX()
        {
            return Task.FromResult(_x);
        }
    }

    public class EchoGenericChainGrain<T> : Grain, IEchoGenericChainGrain<T>
    {
        public async Task<T> Echo(T item)
        {
            long pk = this.GetPrimaryKeyLong();
            var otherGrain = GrainFactory.GetGrain<ISimpleGenericGrain1<T>>(pk);
            await otherGrain.SetA(item);
            return await otherGrain.GetA();
        }

        public async Task<T> Echo2(T item)
        {
            long pk = this.GetPrimaryKeyLong() + 1;
            var otherGrain = GrainFactory.GetGrain<IEchoGenericChainGrain<T>>(pk);
            return await otherGrain.Echo(item);
        }

        public async Task<T> Echo3(T item)
        {
            long pk = this.GetPrimaryKeyLong() + 1;
            var otherGrain = GrainFactory.GetGrain<IEchoGenericChainGrain<T>>(pk);
            return await otherGrain.Echo2(item);
        }

        public async Task<T> Echo4(T item)
        {
            long pk = this.GetPrimaryKeyLong() + 1;
            var otherGrain = GrainFactory.GetGrain<ISimpleGenericGrain1<T>>(pk);
            await otherGrain.SetA(item);
            return await otherGrain.GetA();
        }

        public async Task<T> Echo5(T item)
        {
            long pk = this.GetPrimaryKeyLong() + 1;
            var otherGrain = GrainFactory.GetGrain<IEchoGenericChainGrain<T>>(pk);
            return await otherGrain.Echo4(item);
        }

        public async Task<T> Echo6(T item)
        {
            long pk = this.GetPrimaryKeyLong() + 1;
            var otherGrain = GrainFactory.GetGrain<IEchoGenericChainGrain<T>>(pk);
            return await otherGrain.Echo5(item);
        }
    }

    public class NonGenericBaseGrain : Grain, INonGenericBase
    {
        public Task Ping()
        {
            return TaskDone.Done;
        }
    }

    public class Generic1ArgumentGrain<T> : NonGenericBaseGrain, IGeneric1Argument<T>
    {
        public Task<T> Ping(T t)
        {
            return Task.FromResult(t);
        }
    }

    public class Generic1ArgumentDerivedGrain<T> : NonGenericBaseGrain, IGeneric1Argument<T>
    {
        public Task<T> Ping(T t)
        {
            return Task.FromResult(t);
        }
    }

    public class Generic2ArgumentGrain<T, U> : Grain, IGeneric2Arguments<T, U>
    {
        public Task<Tuple<T, U>> Ping(T t, U u)
        {
            return Task.FromResult(new Tuple<T, U>(t, u));
        }

        public Task Ping()
        {
            return TaskDone.Done;
        }
    }

    public class Generic2ArgumentsDerivedGrain<T, U> : NonGenericBaseGrain, IGeneric2Arguments<T, U>
    {
        public Task<Tuple<T, U>> Ping(T t, U u)
        {
            return Task.FromResult(new Tuple<T, U>(t, u));
        }
    }

    public class DbGrain<T> : Grain, IDbGrain<T>
    {
        private T _value;

        public Task SetValue(T value)
        {
            _value = value;
            return TaskDone.Done;
        }

        public Task<T> GetValue()
        {
            return Task.FromResult(_value);
        }
    }

    [Reentrant]
    public class PingSelfGrain<T> : Grain, IGenericPingSelf<T>
    {
        private T _lastValue;

        public Task<T> Ping(T t)
        {
            _lastValue = t;
            return Task.FromResult(t);
        }

        public Task<T> PingOther(IGenericPingSelf<T> target, T t)
        {
            return target.Ping(t);
        }


        public Task<T> PingSelf(T t)
        {
            return PingOther(this, t);
        }


        public Task<T> PingSelfThroughOther(IGenericPingSelf<T> target, T t)
        {
            return target.PingOther(this, t);
        }

        public Task ScheduleDelayedPing(IGenericPingSelf<T> target, T t, TimeSpan delay)
        {
            RegisterTimer(o =>
            {
                Console.WriteLine("***Timer fired for pinging {0}***", target.GetPrimaryKey());
                return target.Ping(t);
            },
                null,
                delay,
                TimeSpan.FromMilliseconds(-1));
            return TaskDone.Done;
        }


        public Task<T> GetLastValue()
        {
            return Task.FromResult(_lastValue);
        }

        public async Task ScheduleDelayedPingToSelfAndDeactivate(IGenericPingSelf<T> target, T t, TimeSpan delay)
        {
            await target.ScheduleDelayedPing(this, t, delay);
            DeactivateOnIdle();
        }

        public override Task OnActivateAsync()
        {
            Console.WriteLine("***Activating*** {0}", this.GetPrimaryKey());
            return TaskDone.Done;
        }

        public override Task OnDeactivateAsync()
        {
            Console.WriteLine("***Deactivating*** {0}", this.GetPrimaryKey());
            return TaskDone.Done;
        }
    }
}
