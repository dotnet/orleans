using Orleans.Runtime;
using Azure.Messaging.EventHubs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streaming.EventHubs.Testing
{
    /// <summary>
    /// Eventhub receiver which configured with data generator
    /// </summary>
    public class EventHubPartitionGeneratorReceiver : IEventHubReceiver
    {
        private readonly IDataGenerator<EventData> generator;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="generator"></param>
        public EventHubPartitionGeneratorReceiver(IDataGenerator<EventData> generator)
        {
            this.generator = generator;
        }
        /// <inheritdoc />
        public async Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
            => await ReceiveAsync(maxCount, waitTime, CancellationToken.None);

        /// <inheritdoc />
        public async Task<IEnumerable<EventData>> ReceiveAsync(
            int maxCount,
            TimeSpan waitTime,
            CancellationToken cancellationToken)
        {
            IEnumerable<EventData>? events;
            //mimic real life response time
            await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
            if (generator.TryReadEvents(maxCount, out events))
            {
                return events;
            }
            //if no events generated, wait for waitTime to pass
            await Task.Delay(waitTime, cancellationToken);
            return new List<EventData>().AsEnumerable();
        }

        /// <inheritdoc />
        public void StopProducingOnStream(StreamId streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.StopProducingOnStream(streamId);
        }

        /// <inheritdoc />
        public void ConfigureDataGeneratorForStream(StreamId streamId)
        {
            (this.generator as IStreamDataGeneratingController)?.AddDataGeneratorForStream(streamId);
        }

        /// <inheritdoc />
        public Task CloseAsync()
        {
            return Task.CompletedTask;
        }
    }
}
