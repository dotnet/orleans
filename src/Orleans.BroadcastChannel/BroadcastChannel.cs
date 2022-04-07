using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    public interface IBroadcastChannel<T>
    {
        Task Publish(T item);
    }

    internal class BroadcastChannel<T> : IBroadcastChannel<T>
    {
        private readonly InternalChannelId _channelId;
        private readonly IGrainFactory _grainFactory;
        private readonly ImplicitChannelSubscriberTable _subscriberTable;
        private readonly bool _fireAndForgetDelivery;
        private readonly ILogger _logger;

        public BroadcastChannel(
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
            _logger = loggerFactory.CreateLogger($"{nameof(BroadcastChannel<T>)}-{_channelId}");
        }

        public async Task Publish(T item)
        {
            var subscribers = _subscriberTable.GetImplicitSubscribers(_channelId, _grainFactory);

            if (subscribers.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("No consumer found for {Item}", item);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Publishing item {Item} to {ConsumerCount} consumers", item, subscribers.Count);

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
                _logger.LogError(ex, "Exception when sending item to {GrainId}", consumer.GetGrainId());
                if (!_fireAndForgetDelivery)
                {
                    throw;
                }
            }
        }
    }
}

