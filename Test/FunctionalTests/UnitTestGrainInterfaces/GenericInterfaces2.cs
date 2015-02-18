using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTestGrainInterfaces.Generic
{
    public interface INonGenericBase : IGrain
    {
        Task Ping();
    }

    public interface IGeneric1Argument<T> : IGrain
    {
        Task<T> Ping(T t);
    }

    public interface IGeneric2Arguments<T, U> : IGrain
    {
        Task<Tuple<T, U>> Ping(T t, U u);
    }

    //public interface IGenericMethod : INonGenericBase
    //{
    //    Task<T> Ping<T>(T t);
    //}

    public interface IDbGrain<T> : Orleans.IGrain
    {
        Task SetValue(T value);
        Task<T> GetValue();
    }

    public interface IGenericPingSelf<T> : IGrain
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