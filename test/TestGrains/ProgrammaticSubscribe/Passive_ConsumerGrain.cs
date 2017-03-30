﻿using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
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
        private List<IExternalStreamSubscriptionHandle> consumerHandles;
        private int onAddCalledCount;
        public override Task OnActivateAsync()
        {
            logger = base.GetLogger(this.GetType().Name + base.IdentityString);
            logger.Info("OnActivateAsync");
            onAddCalledCount = 0;
            consumerObservers = new List<ICounterObserver>();
            consumerHandles = new List<IExternalStreamSubscriptionHandle>();
            return TaskDone.Done;
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
            return TaskDone.Done;
        }

        public async Task OnAdd<T>(StreamSubscriptionHandle<T> handle)
        {
            logger.Info("OnAdd");
            this.onAddCalledCount++;
            var observer = new CounterObserver<T>(this.logger);
            var newhandle = new ExternalStreamSubscriptionHandle<T>(await handle.ResumeAsync(observer)) as IExternalStreamSubscriptionHandle;
            this.consumerHandles.Add(newhandle);
            this.consumerObservers.Add(observer);
        }
    }

    public class Jerk_ConsumerGrain : Grain, IJerk_ConsumerGrain
    {
        internal Logger logger;

        public override Task OnActivateAsync()
        {
            logger = base.GetLogger("Jerk_ConsumerGrain" + base.IdentityString);
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        //Jerk_ConsumerGrai would unsubscrube on any subscription added to it
        public async Task OnAdd<T>(StreamSubscriptionHandle<T> handle)
        {
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
            this.logger = logger;
        }

        public Task OnNextAsync(T item, StreamSequenceToken token = null)
        {
            this.NumConsumed++;
            this.logger.Info($"Consumer {this.GetHashCode()} OnNextAsync() with NumConsumed {this.NumConsumed}");
            return TaskDone.Done;
        }

        public Task OnCompletedAsync()
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnCompletedAsync()");
            return TaskDone.Done;
        }

        public Task OnErrorAsync(Exception ex)
        {
            this.logger.Info($"Consumer {this.GetHashCode()} OnErrorAsync({ex})");
            return TaskDone.Done;
        }
    }

    [ImplicitStreamSubscription(StreamNameSpace)]
    public class ImplicitSubscribeGrain : Grain, IImplicitSubscribeGrain
    {
        public const string StreamNameSpace = "ImplicitSubscriptionSpace";
    }
}
