using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public interface IGenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> : IGrainWithGuidKey
    {
        Task<Type> GetStateType();
    }

    public class GenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> : Grain<TStateType>,
        IGenericGrainWithGenericState<TFirstTypeParam, TStateType, TLastTypeParam> where TStateType : new()
    {
        public Task<Type> GetStateType() => Task.FromResult(this.State.GetType());
    }

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

    /// <summary>
    /// Long named grain type, which can cause issues in AzureTableStorage
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface ISimpleGenericGrainUsingAzureStorageAndLongGrainName<T> : IGrainWithGuidKey
    {
        Task<T> EchoAsync(T entity);

        Task ClearState();
    }

    /// <summary>
    /// Short named grain type, which shouldn't cause issues in AzureTableStorage
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface ITinyNameGrain<T> : IGrainWithGuidKey
    {
        Task<T> EchoAsync(T entity);

        Task ClearState();
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

    public interface IBasicGenericGrain<T, U> : IGrainWithIntegerKey
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

    public interface ILongRunningTaskGrain<T> : IGrainWithGuidKey
    {
        Task<string> GetRuntimeInstanceId();
        Task<string> GetRuntimeInstanceIdWithDelay(TimeSpan delay);

        Task LongWait(GrainCancellationToken tc, TimeSpan delay);
        Task<T> LongRunningTask(T t, TimeSpan delay);
        Task<T> CallOtherLongRunningTask(ILongRunningTaskGrain<T> target, T t, TimeSpan delay);
        Task<T> FanOutOtherLongRunningTask(ILongRunningTaskGrain<T> target, T t, TimeSpan delay, int degreeOfParallelism);
        Task CallOtherLongRunningTask(ILongRunningTaskGrain<T> target, GrainCancellationToken tc, TimeSpan delay);
        Task CallOtherLongRunningTaskWithLocalToken(ILongRunningTaskGrain<T> target, TimeSpan delay,
            TimeSpan delayBeforeCancel);
        Task<bool> CancellationTokenCallbackResolve(GrainCancellationToken tc);
        Task<bool> CallOtherCancellationTokenCallbackResolve(ILongRunningTaskGrain<T> target);
        Task CancellationTokenCallbackThrow(GrainCancellationToken tc);
        Task<T> GetLastValue();
    }

    [Alias("IGenericGrainWithConstraints`3")]
    public interface IGenericGrainWithConstraints<A, B, C> : IGrainWithStringKey
        where A : ICollection<B>, new() where B : struct where C : class
    {
        [Alias("GetCount")]
        Task<int> GetCount();

        Task Add(B item);

        Task<C> RoundTrip(C value);
    }

    public interface INonGenericCastableGrain : IGrainWithGuidKey
    {
        Task DoSomething();
    }


    public interface IGenericCastableGrain<T> : IGrainWithGuidKey
    { }

    public interface IGenericRegisterGrain<T> : IGrainWithIntegerKey
    {
        Task Set(T value);
        Task<T> Get();
    }

    public interface IGenericArrayRegisterGrain<T> : IGenericRegisterGrain<T[]>
    {
    }

    public interface IGrainSayingHello : IGrainWithGuidKey
    {
        Task<string> Hello();
    }

    public interface ISomeGenericGrain<T> : IGrainSayingHello
    { }

    public interface INonGenericCastGrain : IGrainSayingHello
    { }



    public interface IIndependentlyConcretizedGrain : ISomeGenericGrain<string>
    { }

    public interface IIndependentlyConcretizedGenericGrain<T> : ISomeGenericGrain<T>
    { }


    namespace Generic.EdgeCases
    {
        public interface IBasicGrain : IGrainWithGuidKey
        {
            Task<string> Hello();
            Task<string[]> ConcreteGenArgTypeNames();
        }


        public interface IGrainWithTwoGenArgs<T1, T2> : IBasicGrain
        { }

        public interface IGrainWithThreeGenArgs<T1, T2, T3> : IBasicGrain
        { }

        public interface IGrainReceivingRepeatedGenArgs<T1, T2> : IBasicGrain
        { }

        public interface IPartiallySpecifyingInterface<T> : IGrainWithTwoGenArgs<T, int>
        { }

        public interface IReceivingRepeatedGenArgsAmongstOthers<T1, T2, T3> : IBasicGrain
        { }

        public interface IReceivingRepeatedGenArgsFromOtherInterface<T1, T2, T3> : IBasicGrain
        { }

        public interface ISpecifyingGenArgsRepeatedlyToParentInterface<T> : IReceivingRepeatedGenArgsFromOtherInterface<T, T, T>
        { }

        public interface IReceivingRearrangedGenArgs<T1, T2> : IBasicGrain
        { }

        public interface IReceivingRearrangedGenArgsViaCast<T1, T2> : IBasicGrain
        { }

        public interface ISpecifyingRearrangedGenArgsToParentInterface<T1, T2> : IReceivingRearrangedGenArgsViaCast<T2, T1>
        { }

        public interface IArbitraryInterface<T1, T2> : IBasicGrain
        { }

        public interface IInterfaceUnrelatedToConcreteGenArgs<T> : IBasicGrain
        { }

        public interface IInterfaceTakingFurtherSpecializedGenArg<T> : IBasicGrain
        { }


        public interface IAnotherReceivingFurtherSpecializedGenArg<T> : IBasicGrain
        { }

        public interface IYetOneMoreReceivingFurtherSpecializedGenArg<T> : IBasicGrain
        { }
    }

    public interface IG2<T1, T2> : IGrainWithGuidKey
    { }

    public class HalfOpenGrain1<T> : IG2<T, int>
    { }
    public class HalfOpenGrain2<T> : IG2<int, T>
    { }

    public class OpenGeneric<T2, T1> : IG2<T2, T1>
    { }

    public class ClosedGeneric : IG2<Dummy1, Dummy2>
    { }

    public class ClosedGenericWithManyInterfaces : IG2<Dummy1, Dummy2>, IG2<Dummy2, Dummy1>
    { }

    [GenerateSerializer]
    public class Dummy1 { }

    [GenerateSerializer]
    public class Dummy2 { }

    public interface IG<T> : IGrain
    {
    }

    public class G1<T1, T2, T3, T4> : Grain, Root<T1>.IA<T2, T3, T4>
    {
    }

    public class Root<TRoot>
    {
        public interface IA<T1, T2, T3> : IGrainWithIntegerKey
        {

        }

        public class G<T1, T2, T3> : Grain, IG<IA<T1, T2, T3>>
        {
        }
    }
}
