using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManagerAdmin : IStreamSubscriptionManagerAdmin
    {
        private IStreamSubscriptionManager explicitStreamSubscriptionManager;
        public StreamSubscriptionManagerAdmin(IStreamProviderRuntime providerRuntime)
        {
            // using ExplicitGrainBasedAndImplicit pubsub here, so if StreamSubscriptionManager.Add(Remove)Subscription called on a implicit subscribed
            // consumer grain, its subscription will be handled by ImplicitPubsub, and will not be messed into GrainBasedPubsub 
            this.explicitStreamSubscriptionManager = new StreamSubscriptionManager(providerRuntime.PubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit), 
                StreamSubscriptionManagerType.ExplicitSubscribeOnly);
        }

        public IStreamSubscriptionManager GetStreamSubscriptionManager(StreamSubscriptionManagerType managerType)
        {
            IStreamSubscriptionManager manager = null;
            switch (managerType)
            {
                case StreamSubscriptionManagerType.ExplicitSubscribeOnly:
                    manager = this.explicitStreamSubscriptionManager;
                    break;
                default: manager = this.explicitStreamSubscriptionManager;
                    break;
            }
            return manager;
        }
    }
}
