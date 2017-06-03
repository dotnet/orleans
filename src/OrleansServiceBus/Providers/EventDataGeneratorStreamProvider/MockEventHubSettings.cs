using Orleans.ServiceBus.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers.Testing
{
    /// <summary>
    /// MockEventHubSettings hard code EventHub related settings to avoid configuring those settings. It is used in EventDataGeneratorStreamProvider to mock its connection to EventHub
    /// </summary>
    public class MockEventHubSettings : IEventHubSettings
    {
        /// <summary>
        /// Connection string
        /// </summary>
        public string ConnectionString => "MockConnectionString";
        /// <summary>
        /// EventHub consumer group.
        /// </summary>
        public string ConsumerGroup => "MockConsumerGroup";
        /// <summary>
        /// Hub Path.
        /// </summary>
        public string Path => "MockEventHubPath";
        /// <summary>
        /// Optional parameter which configures the EventHub reciever's prefetch count.
        /// </summary>
        public int? PrefetchCount { get; }

        /// <inheritcdoc/>
        public bool StartFromNow => true;
    }
}
