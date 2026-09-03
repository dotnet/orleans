using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.BroadcastChannel.Diagnostics;
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
        Task Publish([DisallowNull] T item);

        /// <summary>
        /// Publish an element to the channel.
        /// </summary>
        /// <param name="item">The element to publish.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task Publish([DisallowNull] T item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Publish(item);
        }
    }

    /// <inheritdoc />
    internal partial class BroadcastChannelWriter<T> : IBroadcastChannelWriter<T>
    {
        private static readonly string LoggingCategory = typeof(BroadcastChannelWriter<>).FullName!;

        private readonly InternalChannelId _channelId;
        private readonly IGrainFactory _grainFactory;
        private readonly ImplicitChannelSubscriberTable _subscriberTable;
        private readonly bool _fireAndForgetDelivery;
        private readonly ILogger _logger;
        private readonly SiloAddress? _siloAddress;
        private readonly string _clusterId;

        public BroadcastChannelWriter(
            InternalChannelId channelId,
            IGrainFactory grainFactory,
            ImplicitChannelSubscriberTable subscriberTable,
            bool fireAndForgetDelivery,
            ILoggerFactory loggerFactory,
            SiloAddress? siloAddress,
            string clusterId)
        {
            _channelId = channelId;
            _grainFactory = grainFactory;
            _subscriberTable = subscriberTable;
            _fireAndForgetDelivery = fireAndForgetDelivery;
            _logger = loggerFactory.CreateLogger(LoggingCategory);
            _siloAddress = siloAddress;
            _clusterId = clusterId;
        }

        /// <inheritdoc />
        public Task Publish([DisallowNull] T item) => Publish(item, default);

        /// <inheritdoc />
        public async Task Publish([DisallowNull] T item, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subscribers = _subscriberTable.GetImplicitSubscribers(_channelId, _grainFactory);

            if (subscribers.Count == 0)
            {
                LogDebugNoConsumerFound(_logger, item);
                return;
            }

            LogDebugPublishingItem(_logger, item, subscribers.Count);

            BroadcastChannelEvents.EmitItemPublished(_channelId.ProviderName, _channelId.ChannelId, subscribers.Count, _siloAddress, _clusterId);

            if (_fireAndForgetDelivery)
            {
                foreach (var sub in subscribers)
                {
                    PublishToSubscriber(sub.Value, item, cancellationToken).Ignore();
                }
            }
            else
            {
                var tasks = new List<Task>();
                foreach (var sub in subscribers)
                {
                    tasks.Add(PublishToSubscriber(sub.Value, item, cancellationToken));
                }
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    throw new AggregateException(tasks.Select(t => t.Exception!).Where(ex => ex != null));
                }
            }
        }

        private async Task PublishToSubscriber(
            IBroadcastChannelConsumerExtension consumer,
            T item,
            CancellationToken cancellationToken)
        {
            try
            {
                await consumer.OnPublished(_channelId, item!, cancellationToken);
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
