using System.Collections.Generic;
using RabbitMQ.Client;

namespace Orleans.Streams
{
    public class RabbitMQOptions : ConnectionFactory
    {
        //public RabbitMQExchangeOptions ExchangeOptions;
        //public RabbitMQQueueOptions QueueOptions;
        //public RabbitMQBindingOptions BindingOptions;
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

    public class RabbitMQReceiverOptions
    {
        /// <summary>
        /// Optional parameter that configures the receiver prefetch count.
        /// </summary>
        public int? PrefetchCount { get; set; }
        /// <summary>
        /// In cases where no checkpoint is found, this indicates if service should read from the most recent data, or from the beginning of a partition.
        /// </summary>
        public bool StartFromNow { get; set; } = DEFAULT_START_FROM_NOW;
        public const bool DEFAULT_START_FROM_NOW = true;
    }
}
