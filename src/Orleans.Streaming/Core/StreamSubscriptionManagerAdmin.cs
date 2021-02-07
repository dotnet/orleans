using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManagerAdmin : IStreamSubscriptionManagerAdmin
    {
        private Dictionary<string, IStreamSubscriptionManager> managerStore;
        public StreamSubscriptionManagerAdmin(IStreamProviderRuntime providerRuntime)
        {
            // using ExplicitGrainBasedAndImplicit pubsub here, so if StreamSubscriptionManager.Add(Remove)Subscription called on a implicit subscribed
            // consumer grain, its subscription will be handled by ImplicitPubsub, and will not be messed into GrainBasedPubsub 
            var explicitStreamSubscriptionManager = new StreamSubscriptionManager(providerRuntime.PubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit), 
                StreamSubscriptionManagerType.ExplicitSubscribeOnly);
            managerStore = new Dictionary<string, IStreamSubscriptionManager>();
            managerStore.Add(StreamSubscriptionManagerType.ExplicitSubscribeOnly, explicitStreamSubscriptionManager);
        }

        public IStreamSubscriptionManager GetStreamSubscriptionManager(string managerType)
        {
            IStreamSubscriptionManager manager;
            if (this.managerStore.TryGetValue(managerType, out manager))
            {
                return manager;
            }
            else
            {
                throw new KeyNotFoundException($"Cannot find StreamSubscriptionManager with type {managerType}.");
            }
                
        }
    }
}
