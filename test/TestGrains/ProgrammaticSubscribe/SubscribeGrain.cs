using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams.Core;
using Orleans.Streams.PubSub;

namespace UnitTests.Grains.ProgrammaticSubscribe
{
    public interface ISubscribeGrain : IGrainWithGuidKey
    {
        Task<List<StreamSubscription>> SetupStreamingSubscriptionForStream<TGrainInterface>(FullStreamIdentity streamIdentity, int grainCount)
            where TGrainInterface : IGrainWithGuidKey;
        Task RemoveSubscription(StreamSubscription subscription);
        Task<StreamSubscription> AddSubscription<TGrainInterface>(FullStreamIdentity streamId, Guid grainId)
            where TGrainInterface : IGrainWithGuidKey;
        Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity);
        Task<bool> CanGetSubscriptionManager(string providerName);
    }


    public class SubscribeGrain : Grain, ISubscribeGrain
    {
        public async Task<List<StreamSubscription>> SetupStreamingSubscriptionForStream<TGrainInterface>(FullStreamIdentity streamIdentity, int grainCount)
            where TGrainInterface : IGrainWithGuidKey
        {
            var subscriptions = new List<StreamSubscription>();
            while (grainCount > 0)
            {
                var grainId = Guid.NewGuid();
                var grainRef = this.GrainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference;
                var subManager = this.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>().GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
                subscriptions.Add(await subManager.AddSubscription(streamIdentity.ProviderName, streamIdentity, grainRef));
                grainCount--;
            }
            return subscriptions;
        }

        public Task<bool> CanGetSubscriptionManager(string providerName)
        {
            IStreamSubscriptionManager manager;
            return Task.FromResult(this.ServiceProvider.GetService<IStreamProviderManager>().GetStreamProvider(providerName).TryGetStreamSubscrptionManager(out manager));
        }

        public async Task<StreamSubscription> AddSubscription<TGrainInterface>(FullStreamIdentity streamId, Guid grainId)
            where TGrainInterface : IGrainWithGuidKey
        {
            var grainRef = this.GrainFactory.GetGrain<TGrainInterface>(grainId) as GrainReference;
            var sub = await this.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>()
                .GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly)
                .AddSubscription(streamId.ProviderName, streamId, grainRef);
            return sub;
        }

        public Task<IEnumerable<StreamSubscription>> GetSubscriptions(FullStreamIdentity streamIdentity)
        {
            var subManager = this.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>().GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            return subManager.GetSubscriptions(streamIdentity.ProviderName, streamIdentity);
        }

        public async Task RemoveSubscription(StreamSubscription subscription)
        {
            var subManager = this.ServiceProvider.GetService<IStreamSubscriptionManagerAdmin>().GetStreamSubscriptionManager(StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            await subManager.RemoveSubscription(subscription.StreamProviderName, subscription.StreamId, subscription.SubscriptionId);
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
}
