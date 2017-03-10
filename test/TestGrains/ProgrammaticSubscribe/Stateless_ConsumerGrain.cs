using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using System.Collections.Concurrent;
using Microsoft.FSharp.Collections;
using Orleans.Providers;
using Orleans.Streams.Core;
using UnitTests.Grains.ProgrammaticSubscribe;

namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class Stateless_ConsumerGrain : Grain, IStateless_ConsumerGrain
    {
        public const int MaxLocalWorkers = 2;
        internal Logger logger;
        private List<ConsumerObserver<int>> consumerObservers;
        private List<StreamSubscriptionHandle<int>> consumerHandles;
        private int onRemoveCalledCount;
        private int onAddCalledCount;
        
        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("Stateless_ConsumerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            logger.Info("ResumeAsyncOnObserver");
            onRemoveCalledCount = 0;
            onAddCalledCount = 0;
            consumerObservers = new List<ConsumerObserver<int>>();
            consumerHandles = new List<StreamSubscriptionHandle<int>>();
            IStreamProvider streamProvider = base.GetStreamProvider("SMSProvider");
            await streamProvider.OnSubscriptionChange<int>(this.OnAdd, this.OnRemove);
        }

        public Task<int> GetCountOfOnRemoveFuncCalled()
        {
            return Task.FromResult(this.onRemoveCalledCount);
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

        private async Task OnAdd(StreamSubscriptionHandle<int> handle)
        {
            this.onAddCalledCount++;
            var observer = new ConsumerObserver<int>(this.logger);
            var newhandle = await handle.ResumeAsync(observer);
            this.consumerObservers.Add(observer);
            this.consumerHandles.Add(newhandle);
        }

        private Task OnRemove(string providerName, IStreamIdentity streamId, Guid subscriptionId)
        {
            this.onRemoveCalledCount++;
            return TaskDone.Done;
        }
    }


    public class ConsumerObserver<T> : IAsyncObserver<T>
    {
        public int NumConsumed { get; private set; }
        private Logger logger;
        internal ConsumerObserver(Logger logger)
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
