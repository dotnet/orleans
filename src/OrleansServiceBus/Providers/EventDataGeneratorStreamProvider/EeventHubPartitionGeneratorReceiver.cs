#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
using Orleans.Serialization;
#endif
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    internal class EventHubPartitionGeneratorReceiver : IEventHubReceiver
    {
        private IDataGenerator<EventData> generator;
        public EventHubPartitionGeneratorReceiver(IDataGenerator<EventData> generator)
        {
            this.generator = generator;
        }
        public Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            IEnumerable<EventData> events;
            if (generator.TryReadEvents(maxCount, waitTime, out events))
            {
                return Task.FromResult(events);
            }
            return Task.FromResult(new List<EventData>().AsEnumerable());
        }

        public void StopProducingOnStream(IStreamIdentity streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.StopProducingOnStream(streamId);
        }

        public void ConfigureDataGeneratorForStream(IStreamIdentity streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.AddDataGeneratorForStream(streamId);
        }

        public Task CloseAsync()
        {
            return TaskDone.Done;
        }
    }
}
