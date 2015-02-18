using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces.Generic
{
    public interface ISimpleGenericGrain<T> : IGrain
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, T b);
        Task SetA(T a);
        Task SetB(T b);
    }

    public interface ISimpleGenericGrainU<U> : IGrain
    {
        Task<U> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(U a, U b);
        Task SetA(U a);
        Task SetB(U b);
    }

    public interface ISimpleGenericGrain2<T, in U> : IGrain
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, U b);
        Task SetA(T a);
        Task SetB(U b);
    }

    public interface IGenericGrainWithNoProperties<in T> : IGrain
    {
        Task<string> GetAxB(T a, T b);
    }
    public interface IGrainWithNoProperties : IGrain
    {
        Task<string> GetAxB(int a, int b);
    }

    public interface IGrainWithListFields : IGrain
    {
        Task AddItem(string item);
        Task<IList<string>> GetItems();
    }
    public interface IGenericGrainWithListFields<T> : IGrain
    {
        Task AddItem(T item);
        Task<IList<T>> GetItems();
    }

    public interface IGenericReader1<T> : IGrain
    {
        Task<T> GetValue();
    }
    public interface IGenericWriter1<in T> : IGrain
    {
        Task SetValue(T value);
    }
    public interface IGenericReaderWriterGrain1<T> : IGenericWriter1<T>, IGenericReader1<T>
    {
    }

    public interface IGenericReader2<TOne,TTwo> : IGrain
    {
        Task<TOne> GetValue1();
        Task<TTwo> GetValue2();
    }
    public interface IGenericWriter2<in TOne, in TTwo> : IGrain
    {
        Task SetValue1(TOne value);
        Task SetValue2(TTwo value);
    }
    public interface IGenericReaderWriterGrain2<TOne, TTwo> : IGenericWriter2<TOne, TTwo>, IGenericReader2<TOne, TTwo>
    {
    }

    public interface IGenericReader3<TOne, TTwo, TThree> : IGenericReader2<TOne, TTwo>
    {
        //Task<TOne> Value1 { get; }
        //Task<TTwo> Value2 { get; }
        Task<TThree> GetValue3();
    }
    public interface IGenericWriter3<in TOne, in TTwo, in TThree> : IGenericWriter2<TOne, TTwo>
    {
        //Task SetValue1(TOne value);
        //Task SetValue2(TTwo value);
        Task SetValue3(TThree value);
    }
    public interface IGenericReaderWriterGrain3<TOne, TTwo, TThree> : IGenericWriter3<TOne, TTwo, TThree>, IGenericReader3<TOne, TTwo, TThree>
    {
    }

    public interface IGenericSelfManagedGrain<T, U> : IGrain
    {
        Task<T> GetA();
        Task<string> GetAxB();
        Task<string> GetAxB(T a, U b);
        Task SetA(T a);
        Task SetB(U b);
    }

    public interface IHubGrain<TKey, T1, T2> : IGrain
    {
        Task Bar(TKey key, T1 message1, T2 message2);
        
    }

    public interface IEchoHubGrain<TKey, TMessage> : IHubGrain<TKey, TMessage, TMessage>
    {
        Task Foo(TKey key, TMessage message, int x);
        Task<int> GetX();
    }

    public interface IEchoGenericChainGrain<T> : IGrain
    {
        Task<T> Echo(T item);
        Task<T> Echo2(T item);
        Task<T> Echo3(T item);
    }
}
