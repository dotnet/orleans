using System.Collections.Generic;
using RabbitMQ.Client;

namespace Orleans.Providers.RabbitMQ.Streams.RabbitMQ
{
    /// <summary>
    /// Configuration options for listening from a single queue.
    /// </summary>
    public class RabbitMQOptions : ConnectionFactory
    {
        public RabbitMQExchangeOptions ExchangeOptions;
        public RabbitMQQueueOptions QueueOptions;
        public RabbitMQBindingOptions BindingOptions;
    }

    public class RabbitMQExchangeOptions
    {
        /// <summary>
        /// For options, see <seealso cref="ExchangeType" />.
        /// </summary>
        public string ExchangeType;
        public string Name;
        public bool Durable = false;
        public bool AutoDelete = false;
        public IDictionary<string, object> Arguments = null;
    }

    public class RabbitMQQueueOptions
    {
        public string Queue = "";
        public bool Durable = false;
        public bool Exclusive = true;
        public bool AutoDelete = true;
        public IDictionary<string, object> Arguments = null;
    }

    public class RabbitMQBindingOptions
    {
        public string Queue;
        public string Exchange;
        public string RoutingKey;
        public IDictionary<string, object> Arguments = null;
    }
}
