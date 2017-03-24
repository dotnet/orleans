using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Core
{
    internal class StreamSubscriptionManagerAdmin : IStreamSubscriptionManagerAdmin
    {
        private IStreamProviderManager providerManager;
        public StreamSubscriptionManagerAdmin(IStreamProviderManager providerManager)
        {
            this.providerManager = providerManager;
        }

        public IStreamSubscriptionManager GetStreamSubscriptionManager(string name)
        {
            // for now the name is provider name
            var providerName = name;
            var provider = this.providerManager.GetStreamProvider(providerName);
            if (provider is IStreamSubscriptionManagerRetriever)
            {
                return ((IStreamSubscriptionManagerRetriever)provider).GetStreamSubscriptionManager();
            }
            throw new Runtime.OrleansException($"StreamSubscriptionManager with name {name} does not exist");
        }
    }
}
