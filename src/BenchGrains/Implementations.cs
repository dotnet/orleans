using System;
using System.Linq;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.PingPong
{
    [Reentrant]
    public class LongRunningTaskGrain<T> : Grain, ILongRunningTaskGrain<T>
    {
        public async Task<T> CallOtherLongRunningTask(ILongRunningTaskGrain<T> target, T t, TimeSpan delay)
        {
            for (int i = 0; i < 30000; i++)
            {
                target.LongRunningTask(t, delay);

                target.Bar(t);
                target.Foo(t);
            }
            return t;
        }

        public async Task<T> LongRunningTask(T t, TimeSpan delay)
        {
            await Task.Delay(delay);
            DoWork();
            return await Task.FromResult(t);
        }

        private void DoWork()
        {
            int sum = 0;
            for (int i = 0; i < 100; i++)
            {
                sum += i;
            }
        }
        public Task<T> Foo(T t)
        {
            return Task.FromResult(t);
        }
        public Task<T> Bar(T t)
        {
            return Task.FromResult(t);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }
    }
    [Reentrant]
    public class Client : Grain, IClient
    {
        static readonly Message msg = new Message();
        IDestination actor;
        ObserverSubscriptionManager<IClientObserver> subscribers;

        long pings;
        long pongs;
        long repeats;

        public override Task OnActivateAsync()
        {
            subscribers = new ObserverSubscriptionManager<IClientObserver>();
            return TaskDone.Done;
        }

        public Task Initialize(IDestination actor, long repeats)
        {
            this.actor = actor;
            this.repeats = repeats;

            return TaskDone.Done;
        }

        public Task Run()
        {
            actor.Ping(this, msg).Ignore();
            pings++;

            return TaskDone.Done;
        }

        public Task Pong(IDestination @from, Message message)
        {
            pongs++;

            if (pings < repeats)
            {
                actor.Ping(this, msg);
                pings++;
            }
            else if (pongs >= repeats)
            {
                subscribers.Notify(x => x.Done(pings, pongs));
            }

            return TaskDone.Done;
        }

        public Task Subscribe(IClientObserver subscriber)
        {
            subscribers.Subscribe(subscriber);
            return TaskDone.Done;
        }
    }

    public class Destination : Grain, IDestination
    {
        public Task Ping(IClient @from, Message message)
        {
            from.Pong(this, message).Ignore();
            return TaskDone.Done;
        }
    }
}
