using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.ServiceBus.Providers
{
    /// <summary>
    /// Abstraction on EventhubReceiver class, used to configure EventHubReceiver class in EventhubAdapterReceiver,
    /// also used to configure EHGeneratorReceiver in EventHubAdapterReceiver for testing purpose
    /// </summary>
    public interface IEventHubReceiver
    {
        /// <summary>
        /// Send an async message to the partition asking for more messages
        /// </summary>
        /// <param name="maxCount">Max amount of message which should be delivered in this request</param>
        /// <param name="waitTime">Wait time of this request</param>
        /// <returns></returns>
        Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime);

        /// <summary>
        /// Send a clean up message
        /// </summary>
        /// <returns></returns>
        Task CloseAsync();
    }

    /// <summary>
    /// pass through decorator class for EventHubReceiver
    /// </summary>
    internal class EventHubReceiverProxy : IEventHubReceiver
    {
        private readonly PartitionReceiver client;

        public EventHubReceiverProxy(EventHubPartitionSettings partitionSettings, string offset, ILogger logger)
        {
            var receiverOptions = new PartitionReceiverOptions();
            if (partitionSettings.ReceiverOptions.PrefetchCount != null)
            {
                receiverOptions.PrefetchCount = partitionSettings.ReceiverOptions.PrefetchCount.Value;
            }

            receiverOptions.ConnectionOptions = new EventHubConnectionOptions { TransportType = partitionSettings.Hub.EventHubsTransportType };

            var options = partitionSettings.Hub;
            this.client = options.TokenCredential != null
                ? new PartitionReceiver(options.ConsumerGroup, partitionSettings.Partition, GetEventPosition(), options.FullyQualifiedNamespace, options.Path, options.TokenCredential, receiverOptions)
                : new PartitionReceiver(options.ConsumerGroup, partitionSettings.Partition, GetEventPosition(), options.ConnectionString, options.Path, receiverOptions);

            EventPosition GetEventPosition()
            {
                EventPosition eventPosition;

                // If we have a starting offset, read from offset
                if (offset != EventHubConstants.StartOfStream)
                {
                    if (!long.TryParse(offset, out var longOffset))
                    {
                        throw new InvalidOperationException("Offset must be a number.");
                    }

                    logger.LogInformation("Starting to read from EventHub partition {0}-{1} at offset {2}", options.Path, partitionSettings.Partition, offset);
                    eventPosition = EventPosition.FromOffset(longOffset, true);
                }
                // else, if configured to start from now, start reading from most recent data
                else if (partitionSettings.ReceiverOptions.StartFromNow)
                {
                    eventPosition = EventPosition.Latest;
                    logger.LogInformation("Starting to read latest messages from EventHub partition {0}-{1}.", options.Path, partitionSettings.Partition);
                }
                else
                // else, start reading from begining of the partition
                {
                    eventPosition = EventPosition.Earliest;
                    logger.LogInformation("Starting to read messages from begining of EventHub partition {0}-{1}.", options.Path, partitionSettings.Partition);
                }

                return eventPosition;
            }
        }

        public async Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
        {
            return await client.ReceiveBatchAsync(maxCount, waitTime);
        }

        public async Task CloseAsync()
        {
            await client.CloseAsync();
        }
    }
}
