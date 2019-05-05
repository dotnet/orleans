using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.RabbitMQ.Providers
{
    // undone (mxplusb): determine if I need this or not.
    internal class RabbitMQReceiverProxy : IRabbitMQReceiver
    {
        public Task CloseAsync()
        {
            throw new NotImplementedException();
        }

        public Task<IEnumerable<byte[]>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            throw new NotImplementedException();
        }
    }
}
