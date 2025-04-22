using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    /// <summary>
    /// Interface to allow writing to a channel.
    /// </summary>
    /// <typeparam name="T">The channel element type.</typeparam>
    public interface IBroadcastChannelWriter<T>
    {
        /// <summary>
        /// Publish an element to the channel.
        /// </summary>
        /// <param name="item">The element to publish.</param>
        Task Publish(T item);
    }

    /// <inheritdoc />
    internal partial class BroadcastChannelWriter<T> : IBroadcastChannelWriter<T>
    {
        private static readonly string LoggingCategory = typeof(BroadcastChannelWriter<>).FullName;

        private readonly InternalChannelId _channelId;
        private readonly IGrainFactory _grainFactory;
        private readonly ImplicitChannelSubscriberTable _subscriberTable;
        private readonly bool _fireAndForgetDelivery;
        private readonly ILogger _logger;

        public BroadcastChannelWriter(
            InternalChannelId channelId,
            IGrainFactory grainFactory,
            ImplicitChannelSubscriberTable subscriberTable,
            bool fireAndForgetDelivery,
            ILoggerFactory loggerFactory)
        {
            _channelId = channelId;
            _grainFactory = grainFactory;
            _subscriberTable = subscriberTable;
            _fireAndForgetDelivery = fireAndForgetDelivery;
            _logger = loggerFactory.CreateLogger(LoggingCategory);
        }

        /// <inheritdoc />
        public async Task Publish(T item)
        {
            var subscribers = _subscriberTable.GetImplicitSubscribers(_channelId, _grainFactory);

            if (subscribers.Count == 0)
            {
                LogDebugNoConsumerFound(_logger, item);
                return;
            }

            LogDebugPublishingItem(_logger, item, subscribers.Count);

            if (_fireAndForgetDelivery)
            {
                foreach (var sub in subscribers)
                {
                    PublishToSubscriber(sub.Value, item).Ignore();
                }
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var sub in subscribers)
                {
                    tasks.Add(PublishToSubscriber(sub.Value, item));
                }
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception)
                {
                    throw new AggregateException(tasks.Select(t => t.Exception).Where(ex => ex != null));
                }
            }
        }

        private async Task PublishToSubscriber(IBroadcastChannelConsumerExtension consumer, T item)
        {
            try
            {
                await consumer.OnPublished(_channelId, item);
            }
            catch (Exception ex)
            {
                LogErrorExceptionWhenSendingItem(_logger, ex, consumer.GetGrainId());
                if (!_fireAndForgetDelivery)
                {
                    throw;
                }
            }
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "No consumer found for {Item}"
        )]
        private static partial void LogDebugNoConsumerFound(ILogger logger, T item);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Publishing item {Item} to {ConsumerCount} consumers"
        )]
        private static partial void LogDebugPublishingItem(ILogger logger, T item, int consumerCount);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Exception when sending item to {GrainId}"
        )]
        private static partial void LogErrorExceptionWhenSendingItem(ILogger logger, Exception exception, GrainId grainId);
    }
}

