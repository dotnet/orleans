using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Orleans.Streaming.NATS;

/// <summary>
/// Wrapper around a NATS JetStream consumer
/// </summary>
internal sealed class NatsStreamConsumer(
    ILoggerFactory loggerFactory,
    NatsJSContext context,
    string provider,
    string stream,
    uint partition,
    int batchSize,
    INatsDeserialize<NatsStreamMessage> serializer)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<NatsStreamConsumer>();

    private readonly ConsumerConfig _config = new($"orleans-{provider}-{stream}-{partition}")
    {
        FilterSubject = $"{provider}.{partition}.>",
        MaxBatch = batchSize,
        DeliverPolicy = ConsumerConfigDeliverPolicy.All,
        MaxAckPending = batchSize
    };

    private INatsJSConsumer? _consumer;
    private int _consecutiveInitFailures;

    // Log on the 1st attempt, then every 100th to avoid flooding at ~100ms poll cadence.
    private const int InitLogInterval = 100;

    public async Task<(NatsStreamMessage[] Messages, int Count)> GetMessages(int messageCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (this._consumer is null)
        {
            // Lazy retry: attempt re-initialization on each poll cycle.
            // This handles transient failures during initial Initialize()
            // (leader election, timeout, network blip).
            var shouldLog = this._consecutiveInitFailures % InitLogInterval == 0;
            try
            {
                if (shouldLog)
                {
                    this._logger.LogWarning(
                        "NATS Consumer not initialized — attempting re-initialization (attempt {Attempt}). "
                        + "Provider: {Provider} | Stream: {Stream} | Partition: {Partition}.",
                        this._consecutiveInitFailures + 1, provider, stream, partition);
                }

                await Initialize(cancellationToken);
                if (this._consecutiveInitFailures > 0)
                {
                    this._logger.LogInformation(
                        "NATS Consumer re-initialized successfully after {Attempts} failed attempt(s). "
                        + "Provider: {Provider} | Stream: {Stream} | Partition: {Partition}.",
                        this._consecutiveInitFailures, provider, stream, partition);
                }

                this._consecutiveInitFailures = 0;
            }
            catch (Exception ex)
            {
                this._consecutiveInitFailures++;
                if (shouldLog)
                {
                    this._logger.LogWarning(ex,
                        "NATS Consumer re-initialization failed (attempt {Attempt}). "
                        + "Provider: {Provider} | Stream: {Stream} | Partition: {Partition}. Will retry on next poll.",
                        this._consecutiveInitFailures, provider, stream, partition);
                }

                return ([], 0);
            }

            // If still null after retry, bail (next poll will retry again)
            if (this._consumer is null)
            {
                return ([], 0);
            }
        }

        var batchCount = messageCount > 0 && messageCount < batchSize ? messageCount : batchSize;
        var messages = ArrayPool<NatsStreamMessage>.Shared.Rent(batchCount);

        var i = 0;

        await foreach (var msg in this._consumer.FetchNoWaitAsync(
                               new NatsJSFetchOpts { MaxMsgs = batchCount, Expires = TimeSpan.FromSeconds(10) },
                               serializer: serializer)
                           .WithCancellation(cancellationToken))
        {
            var streamMessage = msg.Data;
            if (streamMessage is null)
            {
                this._logger.LogWarning("Unable to deserialize NATS message for subject {Subject}. Ignoring...",
                    msg.Subject);
                continue;
            }

            messages[i] = streamMessage;
            messages[i].ReplyTo = msg.ReplyTo;
            if (msg.Metadata.HasValue)
            {
                messages[i].Sequence = msg.Metadata.Value.Sequence.Stream;
            }

            i++;
        }

        return (messages, i);
    }

    public async Task Initialize(CancellationToken cancellationToken = default)
    {
        var consumer =
            await context.CreateOrUpdateConsumerAsync(stream, this._config, cancellationToken);

        this._consumer = consumer;
    }
}
