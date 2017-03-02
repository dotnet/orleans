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

namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class Stateless_ConsumerGrain : SampleStreaming_ConsumerGrain, IStateless_ConsumerGrain
    {
        public const int MaxLocalWorkers = 2;

        private List<StreamSubscriptionHandle<int>> consumerHandles;
        public override async Task OnActivateAsync()
        {
            logger = base.GetLogger("Stateless_ConsumerGrain " + base.IdentityString);
            logger.Info("OnActivateAsync");
            numConsumedItems = 0;
            consumerHandle = null;
            logger.Info("ResumeAsyncOnObserver");
            consumerObserver = new SampleConsumerObserver<int>(this);
            consumerHandles = new List<StreamSubscriptionHandle<int>>();
            var subGrain = this.GrainFactory.GetGrain<ISubscribeGrain>(SubscribeGrain.SubscribeGrainId);
            var streamIds = await subGrain.GetStreamIdForConsumerGrain(this.GetPrimaryKey());
            foreach (var streamId in streamIds)
            {
                IStreamProvider streamProvider = base.GetStreamProvider(streamId.ProviderName);
                IAsyncStream<int> stream = streamProvider.GetStream<int>(streamId.Guid, streamId.Namespace);
                foreach (var handle in await stream.GetAllSubscriptionHandles())
                {
                    var newHandle = await handle.ResumeAsync(consumerObserver);
                    consumerHandles.Add(newHandle);
                }
            }
        }

        public async Task StopConsuming()
        {
            logger.Info("StopConsuming");
            foreach (var handle in consumerHandles)
            {
                await handle.UnsubscribeAsync();
            }
            consumerHandles.Clear();
        }
    }

    public interface ISubscribeGrain : IGrainWithGuidKey
    {
        Task<List<StreamSubscription>> SetupInitialStreamingSubscriptionForTests(FullStreamIdentity streamIdentity, int grainCount);
        Task<List<Guid>> GetConsumerGrains();
        Task<List<FullStreamIdentity>> GetStreamIdForConsumerGrain(Guid grainId);
        Task RemoveSubscription(StreamSubscription subscription);
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity);
        Task ClearStateAfterTesting();
    }


    [StorageProvider(ProviderName = "Default")]
    public class SubscribeGrain : Grain<StreamingConfig>, ISubscribeGrain
    {
        //one test should use the same SubscribeGrain to set up and retrieve subscribe info
        public static Guid SubscribeGrainId = new Guid("936DA01F-9ABD-4d9d-80C7-02AF85C822A8");
        public async Task<List<StreamSubscription>> SetupInitialStreamingSubscriptionForTests(FullStreamIdentity streamIdentity, int grainCount)
        {
            var streamingConfig = this.State;
            SetUpStreamingConfig(streamingConfig, streamIdentity, grainCount);
            var subscriptions = new List<StreamSubscription>();
            foreach (var pair in streamingConfig.GrainIdToStreamIdMap)
            {
                var grainId = pair.Key;
                var grainRef = this.GrainFactory.GetGrain<IStateless_ConsumerGrain>(grainId) as GrainReference;
                foreach (var streamId in pair.Value)
                {
                    var provider = this.ServiceProvider.GetService<IStreamProviderManager>().GetStreamProvider(streamId.ProviderName);
                    subscriptions.Add(await provider.StreamSubscriptionManager.AddSubscription(streamId, grainRef));
                }
            }
            await this.WriteStateAsync();
            return subscriptions;
        }

        public async Task ClearStateAfterTesting()
        {
            await this.ClearStateAsync();
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity)
        {
            var provider = this.ServiceProvider.GetService<IStreamProviderManager>().GetStreamProvider(streamIdentity.ProviderName);
            return provider.StreamSubscriptionManager.GetSubscriptions(streamIdentity);
        }

        public async Task RemoveSubscription(StreamSubscription subscription)
        {
            var provider = this.ServiceProvider.GetService<IStreamProviderManager>().GetStreamProvider(subscription.StreamProviderName);
            await provider.StreamSubscriptionManager.RemoveSubscription(subscription.StreamId, subscription.SubscriptionId);
        }

        public Task<List<FullStreamIdentity>> GetStreamIdForConsumerGrain(Guid grainId)
        {
            var streamingConfig = this.State;
            List<FullStreamIdentity> streamIds;
            var re = streamingConfig.GrainIdToStreamIdMap.TryGetValue(grainId, out streamIds);
            if(!re)
                throw new OrleansException( $"Getting stream ids for grain {grainId} failed");
            return Task.FromResult<List<FullStreamIdentity>>(streamIds);
        }

        public Task<List<Guid>> GetConsumerGrains()
        {
            var streamingConfig = this.State;
            return Task.FromResult<List<Guid>>(streamingConfig.GrainIdToStreamIdMap.Keys.ToList());
        }

        private static void SetUpStreamingConfig(StreamingConfig streamingConfig, FullStreamIdentity streamIdentity, int grainCount)
        {
            //currently just one stream
            var streamId = streamIdentity;
            while(grainCount > 0)
            {
                // currently just configure one stream for one grainId 
                streamingConfig.GrainIdToStreamIdMap.Add(Guid.NewGuid(), new List<FullStreamIdentity> { streamId });
                grainCount--;
            }
        }
    }

    [Serializable]
    public class FullStreamIdentity : IStreamIdentity
    {
        public FullStreamIdentity(Guid streamGuid, string streamNamespace, string providerName)
        {
            Guid = streamGuid;
            Namespace = streamNamespace;
            this.ProviderName = providerName;
        }

        public string ProviderName;
        /// <summary>
        /// Stream primary key guid.
        /// </summary>
        public Guid Guid { get; }

        /// <summary>
        /// Stream namespace.
        /// </summary>
        public string Namespace { get; }
    }

    public class StreamingConfig
    {
       
        public Dictionary<Guid, List<FullStreamIdentity>> GrainIdToStreamIdMap { get; private set; }

        public StreamingConfig()
        {
            GrainIdToStreamIdMap = new Dictionary<Guid, List<FullStreamIdentity>>();
        }
    }
}
