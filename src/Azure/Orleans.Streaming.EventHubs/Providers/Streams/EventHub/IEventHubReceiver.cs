using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Primitives;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Streaming.EventHubs
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
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime);

        /// <summary>
        /// Sends an asynchronous request for more messages from the partition.
        /// </summary>
        /// <param name="maxCount">The maximum number of messages to return.</param>
        /// <param name="waitTime">The maximum wait time.</param>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>The received messages.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task<IEnumerable<EventData>> ReceiveAsync(
            int maxCount,
            TimeSpan waitTime,
            CancellationToken cancellationToken) => ReceiveAsync(maxCount, waitTime);
#pragma warning restore CS0618

        /// <summary>
        /// Send a clean up message
        /// </summary>
        /// <returns></returns>
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        Task CloseAsync();

        /// <summary>
        /// Sends a cleanup message which can be canceled.
        /// </summary>
        /// <param name="cancellationToken">The token used to cancel the operation.</param>
        /// <returns>A task representing the operation.</returns>
#pragma warning disable CS0618 // Required for compatibility with providers which only implement the legacy overload.
        Task CloseAsync(CancellationToken cancellationToken) => CloseAsync();
#pragma warning restore CS0618
    }

    /// <summary>
    /// pass through decorator class for EventHubReceiver
    /// </summary>
    internal partial class EventHubReceiverProxy : IEventHubReceiver, IQueueAdapterReceiverReadRecovery
    {
        private readonly Func<EventPosition, PartitionReceiver> clientFactory;
        private readonly EventHubConnection? connection;
        private PartitionReceiver client;
        private EventPosition readPosition;
        private bool captureLatestPosition;
        private bool captureBeginningPosition;
        private long? firstSequenceNumber;
        private bool recreateRequired;

        public EventHubReceiverProxy(EventHubPartitionSettings partitionSettings, string offset, ILogger logger)
        {
            var receiverOptions = new PartitionReceiverOptions();
            if (partitionSettings.ReceiverOptions.PrefetchCount != null)
            {
                receiverOptions.PrefetchCount = partitionSettings.ReceiverOptions.PrefetchCount.Value;
            }

            var options = partitionSettings.Hub;
            receiverOptions.ConnectionOptions = options.ConnectionOptions;
            var receiverConnection = options.CreateConnection(options.ConnectionOptions);
            connection = receiverConnection;
            clientFactory = position => new PartitionReceiver(options.ConsumerGroup, partitionSettings.Partition, position, receiverConnection, receiverOptions);
            readPosition = GetEventPosition();
            captureLatestPosition = offset == EventHubConstants.StartOfStream && partitionSettings.ReceiverOptions.StartFromNow;
            captureBeginningPosition = offset == EventHubConstants.StartOfStream && !partitionSettings.ReceiverOptions.StartFromNow;
            client = clientFactory(readPosition);

            EventPosition GetEventPosition()
            {
                EventPosition eventPosition;

                // If we have a starting offset, read from offset
                if (offset != EventHubConstants.StartOfStream)
                {
                    LogInfoStartingRead(logger, options.EventHubName, partitionSettings.Partition, offset);
                    eventPosition = EventPosition.FromOffset(offset, true);
                }
                // else, if configured to start from now, start reading from most recent data
                else if (partitionSettings.ReceiverOptions.StartFromNow)
                {
                    eventPosition = EventPosition.Latest;
                    LogInfoStartingReadLatest(logger, options.EventHubName, partitionSettings.Partition);
                }
                else
                // else, start reading from begining of the partition
                {
                    eventPosition = EventPosition.Earliest;
                    LogInfoStartingReadBegin(logger, options.EventHubName, partitionSettings.Partition);
                }

                return eventPosition;
            }
        }

        internal EventHubReceiverProxy(
            Func<EventPosition, PartitionReceiver> clientFactory,
            EventPosition readPosition,
            bool captureLatestPosition)
        {
            this.clientFactory = clientFactory;
            this.readPosition = readPosition;
            this.captureLatestPosition = captureLatestPosition;
            captureBeginningPosition = !captureLatestPosition && readPosition.Equals(EventPosition.Earliest);
            client = clientFactory(readPosition);
        }

        internal async Task InitializeAsync(CancellationToken cancellationToken)
        {
            if (captureLatestPosition || captureBeginningPosition)
            {
                var properties = await client.GetPartitionPropertiesAsync(cancellationToken);
                if (properties.IsEmpty)
                {
                    readPosition = EventPosition.Earliest;
                    firstSequenceNumber = 0;
                }
                else if (captureLatestPosition)
                {
                    readPosition = EventPosition.FromOffset(properties.LastEnqueuedOffsetString, false);
                }
                else
                {
                    readPosition = EventPosition.FromSequenceNumber(properties.BeginningSequenceNumber, true);
                    firstSequenceNumber = properties.BeginningSequenceNumber;
                }
                captureLatestPosition = false;
                captureBeginningPosition = false;
                recreateRequired = true;
            }

            if (recreateRequired)
            {
                await client.CloseAsync(cancellationToken);
                client = clientFactory(readPosition);
                recreateRequired = false;
            }
        }

        public Task RecoverReadAsync(CancellationToken cancellationToken)
        {
            recreateRequired = true;
            return InitializeAsync(cancellationToken);
        }

        public async Task<IEnumerable<EventData>> ReceiveAsync(int maxCount, TimeSpan waitTime)
            => await ReceiveAsync(maxCount, waitTime, CancellationToken.None);

        public async Task<IEnumerable<EventData>> ReceiveAsync(
            int maxCount,
            TimeSpan waitTime,
            CancellationToken cancellationToken)
        {
            await InitializeAsync(cancellationToken);
            try
            {
                var messages = (await client.ReceiveBatchAsync(maxCount, waitTime, cancellationToken)).ToArray();
                if (messages.Length > 0)
                {
                    if (firstSequenceNumber is { } expected && messages[0].SequenceNumber != expected)
                    {
                        throw new InvalidOperationException($"The Event Hubs read started at sequence {messages[0].SequenceNumber} instead of the captured partition boundary {expected}.");
                    }

                    readPosition = EventPosition.FromOffset(messages[^1].OffsetString, false);
                    firstSequenceNumber = null;
                }
                return messages;
            }
            catch
            {
                recreateRequired = true;
                throw;
            }
        }

        public Task CloseAsync() => CloseAsync(CancellationToken.None);

        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await client.CloseAsync(cancellationToken);
            }
            finally
            {
                if (connection is not null) await connection.CloseAsync(cancellationToken);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Starting to read from EventHub partition {EventHubName}-{Partition} at offset {Offset}"
        )]
        private static partial void LogInfoStartingRead(ILogger logger, string eventHubName, string partition, string offset);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Starting to read latest messages from EventHub partition {EventHubName}-{Partition}."
        )]
        private static partial void LogInfoStartingReadLatest(ILogger logger, string eventHubName, string partition);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Starting to read messages from begining of EventHub partition {EventHubName}-{Partition}."
        )]
        private static partial void LogInfoStartingReadBegin(ILogger logger, string eventHubName, string partition);
    }
}
