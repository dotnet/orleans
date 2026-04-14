using System;
using System.Linq;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;
using Orleans.Serialization;

namespace Orleans.Streaming.NATS;

internal sealed partial class NatsQueueAdapterReceiver : IQueueAdapterReceiver
{
    private readonly ILogger _logger;
    private readonly uint _partition;
    private readonly string _providerName;
    private readonly Serializer _serializer;
    private NatsConnectionManager? _nats;
    private NatsStreamConsumer? _consumer;
    private Task? _outstandingTask;

    public static IQueueAdapterReceiver Create(string providerName, ILoggerFactory loggerFactory,
        NatsConnectionManager connectionManager, uint partition,
        NatsOptions options, Serializer serializer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        return new NatsQueueAdapterReceiver(providerName, loggerFactory, partition, connectionManager, serializer);
    }

    private NatsQueueAdapterReceiver(string providerName, ILoggerFactory loggerFactory, uint partition,
        NatsConnectionManager nats, Serializer serializer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(nats);

        this._logger = loggerFactory.CreateLogger<NatsQueueAdapterReceiver>();
        this._nats = nats;
        this._partition = partition;
        this._providerName = providerName;
        this._serializer = serializer;
    }

    public async Task Initialize(TimeSpan timeout)
    {
        // If it is null, then we are shutting down
        if (this._nats is null) return;

        using var cts = new CancellationTokenSource(timeout);

        this._consumer = this._nats.CreateConsumer(this._partition);
        if (this._consumer is null)
        {
            this.LogUnableToCreateConsumer(this._partition);
            return;
        }

        await this._consumer.Initialize(cts.Token);
    }

    public async Task Shutdown(TimeSpan timeout)
    {
        try
        {
            if (this._outstandingTask is not null)
            {
                await this._outstandingTask;
            }
        }
        finally
        {
            this._consumer = null;
            this._nats = null;
        }
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        try
        {
            if (this._nats is null || this._consumer is null)
            {
                this.LogProviderNotInitializedForReceiving(this._providerName, this._partition);
                return [];
            }

            var task = this._consumer.GetMessages(maxCount);
            this._outstandingTask = task;
            var (messages, messageCount) = await task;

            var containers = new List<IBatchContainer>(messageCount);

            for (var i = 0; i < messageCount; i++)
            {
                var natsMessage = messages[i];
                var container = this._serializer.Deserialize<NatsBatchContainer>(natsMessage.Payload);
                container.SequenceToken = new EventSequenceTokenV2((long)natsMessage.Sequence);
                container.ReplyTo = natsMessage.ReplyTo;

                containers.Add(container);
            }

            ArrayPool<NatsStreamMessage?>.Shared.Return(messages);

            return containers;
        }
        finally
        {
            this._outstandingTask = null;
        }
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        if (this._nats is null || this._consumer is null)
        {
            this.LogProviderNotInitializedForDelivery(this._providerName, this._partition);
            return;
        }

        if (messages.Count == 0) return;

        var tasks = new List<Task>();

        foreach (var message in messages)
        {
            if (message is NatsBatchContainer natsMessage && !string.IsNullOrWhiteSpace(natsMessage.ReplyTo))
            {
                tasks.Add(this._nats.AcknowledgeMessages(natsMessage.ReplyTo));
            }
        }

        await Task.WhenAll(tasks);
    }

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Unable to create a consumer for partition '{Partition}'.")]
    private partial void LogUnableToCreateConsumer(uint partition);

    [LoggerMessage(2, LogLevel.Warning, "NATS provider '{Provider}' is not initialized for partition '{Partition}', so messages cannot be received. This is expected during shutdown; otherwise, provider initialization failed.")]
    private partial void LogProviderNotInitializedForReceiving(string provider, uint partition);

    [LoggerMessage(3, LogLevel.Warning, "NATS provider '{Provider}' is not initialized for partition '{Partition}', so delivered messages cannot be acknowledged. This is expected during shutdown; otherwise, provider initialization failed.")]
    private partial void LogProviderNotInitializedForDelivery(string provider, uint partition);

    #endregion Logging
}
