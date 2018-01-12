using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.Streams.Core;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class Passive_ConsumerGrain : Grain, IPassive_ConsumerGrain
    {
        internal Logger logger;
        private List<ICounterObserver> consumerObservers;
        private List<StreamSubscriptionHandle<IFruit>> consumerHandles;
        private int onAddCalledCount;
        public override Task OnActivateAsync()
        {
            logger = this.GetLogger(this.GetType().Name + base.IdentityString);
            logger.Info("OnActivateAsync");
            onAddCalledCount = 0;
            consumerObservers = new List<ICounterObserver>();
            consumerHandles = new List<StreamSubscriptionHandle<IFruit>>();
            return Task.CompletedTask;
        }

        public Task<int> GetCountOfOnAddFuncCalled()
        {
            return Task.FromResult(this.onAddCalledCount);
        }

        public Task<int> GetNumberConsumed()
        {
            int sum = 0;
            foreach (var observer in consumerObservers)
            {
                sum += observer.NumConsumed;
            }

            logger.Info($"GetNumberConsumed {sum}");
            return Task.FromResult(sum);
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            foreach (var handle in consumerHandles)
            {
                await handle.UnsubscribeAsync();
            }
            consumerHandles.Clear();
            consumerObservers.Clear();
        }

        public override Task OnDeactivateAsync()
        {
            logger.Info("OnDeactivateAsync");
            return Task.CompletedTask;
        }

        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            logger.Info("OnAdd");
            this.onAddCalledCount++;
            var observer = new CounterObserver<IFruit>(this.logger);
            var newhandle = handleFactory.Create<IFruit>();
            this.consumerHandles.Add(await newhandle.ResumeAsync(observer));
            this.consumerObservers.Add(observer);
        }
    }

    public class Jerk_ConsumerGrain : Grain, IJerk_ConsumerGrain
    {
        internal Logger logger;
        public override Task OnActivateAsync()
        {
            logger = this.GetLogger("Jerk_ConsumerGrain" + base.IdentityString);
            logger.Info("OnActivateAsync");
            return Task.CompletedTask;
        }

        //Jerk_ConsumerGrai would unsubscrube on any subscription added to it
        public async Task OnSubscribed(IStreamSubscriptionHandleFactory handleFactory)
        {
            var handle = handleFactory.Create<int>();
            await handle.UnsubscribeAsync();
        }
    }

    public interface ICounterObserver
    {
        int NumConsumed { get; }
    }

    public class CounterObserver<T> : IAsyncObserver<T>, ICounterObserver
    {
        public int NumConsumed { get; private set; }
        private Logger logger;
        internal CounterObserver(Logger logger)
        {
            this.NumConsumed = 0;
            this.logger = logger.GetSubLogger(this.GetType().Name);
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            this.NumConsumed++;
            this.logger.Info($"Consumer {this.GetHashCode()} OnNextAsync() with NumConsumed {this.NumConsumed}");
            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnCompletedAsync()");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnErrorAsync({ex})");
            return Task.CompletedTask;
        }
    }

    [ImplicitStreamSubscription(StreamNameSpace)]
    public class ImplicitSubscribeGrain : Grain, IImplicitSubscribeGrain
    {
        public const string StreamNameSpace = "ImplicitSubscriptionSpace";
    }
}
