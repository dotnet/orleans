using System.Collections.Generic;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManagerAdmin : IStreamSubscriptionManagerAdmin
    {
        private readonly StreamSubscriptionManager _explicitStreamSubscriptionManager;

        public StreamSubscriptionManagerAdmin(IStreamProviderRuntime providerRuntime)
        {
            // using ExplicitGrainBasedAndImplicit pubsub here, so if StreamSubscriptionManager.Add(Remove)Subscription called on a implicit subscribed
            // consumer grain, its subscription will be handled by ImplicitPubsub, and will not be messed into GrainBasedPubsub 
            _explicitStreamSubscriptionManager = new StreamSubscriptionManager(providerRuntime.PubSub(StreamPubSubType.ExplicitGrainBasedAndImplicit), 
                StreamSubscriptionManagerType.ExplicitSubscribeOnly);
        }

        public IStreamSubscriptionManager GetStreamSubscriptionManager(string managerType)
        {
            return managerType switch
            {
                StreamSubscriptionManagerType.ExplicitSubscribeOnly => _explicitStreamSubscriptionManager,
                _ => throw new KeyNotFoundException($"Cannot find StreamSubscriptionManager with type {managerType}.")
            };
        }
    }
}
