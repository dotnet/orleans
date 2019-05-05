using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.RabbitMQ.Providers
{
    public interface IRabbitMQReceiver
    {
        /// <summary>
        /// Send an async message to the partition asking for more messages.
        /// </summary>
        /// <param name="maxCount">Max amount of messages which should be delivered in the passed time span.</param>
        /// <param name="waitTime">Wait time of this request.</param>
        /// <returns></returns>
        Task<IEnumerable<byte[]>> ReceiveAsync(int maxCount, TimeSpan waitTime);

        /// <summary>
        /// Send a clean up message.
        /// </summary>
        /// <returns></returns>
        Task CloseAsync();
    }
}
