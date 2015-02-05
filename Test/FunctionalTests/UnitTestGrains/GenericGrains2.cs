using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using UnitTestGrainInterfaces.Generic;

namespace GenericTestGrains
{

    public class NonGenericBaseGrain : Grain, INonGenericBase
    {
        public Task Ping()
        {
            return TaskDone.Done;
        }
    }

    //public class GenericMethod : NonGenericBaseGrain, IGeneric1Argument<T>
    //{
    //    public Task<T> Ping<T>(T t)
    //    {
    //        return Task.FromResult(t);
    //    }
    //}

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
            return Task.FromResult(new Tuple<T, U>(t,u));
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