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

namespace UnitTests.GrainInterfaces
{
    public interface IGenericGrain<T, U> : IGrainWithIntegerKey
    {
        Task SetT(T a);
        Task<U> MapT2U();
    }

    public interface ISimpleGenericGrain1<T> : IGrainWithIntegerKey
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, T b);
        Task SetA(T a);
        Task SetB(T b);
    }

    public interface ISimpleGenericGrainU<U> : IGrainWithIntegerKey
    {
        Task<U> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(U a, U b);
        Task SetA(U a);
        Task SetB(U b);
    }

    public interface ISimpleGenericGrain2<T, in U> : IGrainWithIntegerKey
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, U b);
        Task SetA(T a);
        Task SetB(U b);
    }

    public interface IGenericGrainWithNoProperties<in T> : IGrainWithIntegerKey
    {
        Task<string> GetAxB(T a, T b);
    }
    public interface IGrainWithNoProperties : IGrainWithIntegerKey
    {
        Task<string> GetAxB(int a, int b);
    }

    public interface IGrainWithListFields : IGrainWithIntegerKey
    {
        Task AddItem(string item);
        Task<IList<string>> GetItems();
    }
    public interface IGenericGrainWithListFields<T> : IGrainWithIntegerKey
    {
        Task AddItem(T item);
        Task<IList<T>> GetItems();
    }

    public interface IGenericReader1<T> : IGrainWithIntegerKey
    {
        Task<T> GetValue();
    }
    public interface IGenericWriter1<in T> : IGrainWithIntegerKey
    {
        Task SetValue(T value);
    }
    public interface IGenericReaderWriterGrain1<T> : IGenericWriter1<T>, IGenericReader1<T>
    {
    }

    public interface IGenericReader2<TOne, TTwo> : IGrainWithIntegerKey
    {
        Task<TOne> GetValue1();
        Task<TTwo> GetValue2();
    }
    public interface IGenericWriter2<in TOne, in TTwo> : IGrainWithIntegerKey
    {
        Task SetValue1(TOne value);
        Task SetValue2(TTwo value);
    }
    public interface IGenericReaderWriterGrain2<TOne, TTwo> : IGenericWriter2<TOne, TTwo>, IGenericReader2<TOne, TTwo>
    {
    }

    public interface IGenericReader3<TOne, TTwo, TThree> : IGenericReader2<TOne, TTwo>
    {
        Task<TThree> GetValue3();
    }
    public interface IGenericWriter3<in TOne, in TTwo, in TThree> : IGenericWriter2<TOne, TTwo>
    {
        Task SetValue3(TThree value);
    }
    public interface IGenericReaderWriterGrain3<TOne, TTwo, TThree> : IGenericWriter3<TOne, TTwo, TThree>, IGenericReader3<TOne, TTwo, TThree>
    {
    }

    public interface IGenericSelfManagedGrain<T, U> : IGrainWithIntegerKey
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, U b);
        Task SetA(T a);
        Task SetB(U b);
    }

    public interface IHubGrain<TKey, T1, T2> : IGrainWithIntegerKey
    {
        Task Bar(TKey key, T1 message1, T2 message2);
        
    }

    public interface IEchoHubGrain<TKey, TMessage> : IHubGrain<TKey, TMessage, TMessage>
    {
        Task Foo(TKey key, TMessage message, int x);
        Task<int> GetX();
    }

    public interface IEchoGenericChainGrain<T> : IGrainWithIntegerKey
    {
        Task<T> Echo(T item);
        Task<T> Echo2(T item);
        Task<T> Echo3(T item);
        Task<T> Echo4(T item);
        Task<T> Echo5(T item);
        Task<T> Echo6(T item);
    }

    public interface INonGenericBase : IGrainWithGuidKey
    {
        Task Ping();
    }

    public interface IGeneric1Argument<T> : IGrainWithGuidKey
    {
        Task<T> Ping(T t);
    }

    public interface IGeneric2Arguments<T, U> : IGrainWithIntegerKey
    {
        Task<Tuple<T, U>> Ping(T t, U u);
    }

    public interface IDbGrain<T> : IGrainWithIntegerKey
    {
        Task SetValue(T value);
        Task<T> GetValue();
    }

    public interface IGenericPingSelf<T> : IGrainWithGuidKey
    {
        Task<T> Ping(T t);
        Task<T> PingSelf(T t);
        Task<T> PingOther(IGenericPingSelf<T> target, T t);
        Task<T> PingSelfThroughOther(IGenericPingSelf<T> target, T t);
        Task<T> GetLastValue();
        Task ScheduleDelayedPing(IGenericPingSelf<T> target, T t, TimeSpan delay);
        Task ScheduleDelayedPingToSelfAndDeactivate(IGenericPingSelf<T> target, T t, TimeSpan delay);
    }
}
