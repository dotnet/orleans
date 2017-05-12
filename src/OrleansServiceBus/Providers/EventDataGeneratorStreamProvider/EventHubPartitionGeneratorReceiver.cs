#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
using Orleans.Serialization;
#endif
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers.Testing
{
    /// <summary>
    /// Eventhub receiver which configured with data generator
    /// </summary>
    public class EventHubPartitionGeneratorReceiver : IEventHubReceiver
    {
        private IDataGenerator<EventData> generator;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="generator"></param>
        public EventHubPartitionGeneratorReceiver(IDataGenerator<EventData> generator)
        {
            this.generator = generator;
        }
        /// <inheritdoc cref="IEventHubReceiver"/>
        public async Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            IEnumerable<EventData> events;
            //mimic real life response time
            await Task.Delay(TimeSpan.FromMilliseconds(30));
            if (generator.TryReadEvents(maxCount, out events))
            {
                return events;
            }
            //if no events generated, wait for waitTime to pass
            await Task.Delay(waitTime);
            return new List<EventData>().AsEnumerable();
        }

        /// <inheritdoc cref="IEventHubReceiver"/>
        public void StopProducingOnStream(IStreamIdentity streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.StopProducingOnStream(streamId);
        }

        /// <inheritdoc cref="IEventHubReceiver"/>
        public void ConfigureDataGeneratorForStream(IStreamIdentity streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.AddDataGeneratorForStream(streamId);
        }

        /// <inheritdoc cref="IEventHubReceiver"/>
        public Task CloseAsync()
        {
            return Task.CompletedTask;
        }
    }
}
