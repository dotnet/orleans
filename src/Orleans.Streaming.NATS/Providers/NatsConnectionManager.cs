using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Client.Serializers.Json;

namespace Orleans.Streaming.NATS;

/// <summary>
/// Wrapper around NATS and JetStream APIs
/// </summary>
internal sealed class NatsConnectionManager
{
    private static readonly byte[] AckPayload = "+ACK"u8.ToArray();
    private readonly string _providerName;
    private readonly NatsOpts _natsClientOptions;
    private readonly NatsConnection _natsConnection;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NatsOptions _options;
    private readonly NatsJSContext[] _producerNatsContexts;
    private readonly NatsJSContext _natsContext;

    [GeneratedActivatorConstructor]
    public NatsConnectionManager(string providerName, ILoggerFactory loggerFactory, NatsOptions options)
    {
        this._providerName = providerName;
        this._loggerFactory = loggerFactory;
        this._logger = this._loggerFactory.CreateLogger<NatsConnectionManager>();
        this._options = options;
        this._options.JsonSerializerOptions ??= new();
        this._options.JsonSerializerOptions.TypeInfoResolverChain.Add(NatsSerializerContext.Default);

        if (this._options.NatsClientOptions is null)
        {
            this._options.NatsClientOptions = NatsOpts.Default with
            {
                Name = $"Orleans-{this._providerName}",
                SerializerRegistry =
                new NatsJsonContextOptionsSerializerRegistry(this._options.JsonSerializerOptions)
            };
        }
        else
        {
            this._options.NatsClientOptions = this._options.NatsClientOptions with
            {
                Name = string.IsNullOrWhiteSpace(this._options.NatsClientOptions.Name)
                    ? $"Orleans-{this._providerName}"
                    : this._options.NatsClientOptions.Name,
                SerializerRegistry = new NatsJsonContextOptionsSerializerRegistry(this._options.JsonSerializerOptions)
            };
        }

        this._natsClientOptions = this._options.NatsClientOptions;
        this._natsConnection = new NatsConnection(this._natsClientOptions);
        this._natsContext = new NatsJSContext(this._natsConnection);

        this._producerNatsContexts = new NatsJSContext[this._options.ProducerCount];

        for (var i = 0; i < this._options.ProducerCount; i++)
        {
            var producerOptions = this._natsClientOptions with { Name = $"Orleans-{this._providerName}-Producer-{i}" };
            var producerConnection = new NatsConnection(producerOptions);
            this._producerNatsContexts[i] = new NatsJSContext(producerConnection);
        }
    }

    /// <summary>
    /// Initialize the connection to the NATS server and check if JetStream is available
    /// </summary>
    public async Task Initialize(CancellationToken cancellationToken = default)
    {
        try
        {
            await this._natsConnection.ConnectAsync();

            if (this._natsConnection.ConnectionState != NatsConnectionState.Open)
            {
                this._logger.LogError("Unable to connect to NATS server {NatsServer}", this._natsClientOptions.Url);
                return;
            }

            if (!this._natsConnection.ServerInfo!.JetStreamAvailable)
            {
                this._logger.LogError(
                    "Unable to use {NatsServer} for Orleans Stream Provider {ProviderName}: NATS JetStream is not available",
                    this._natsClientOptions.Url, this._providerName);
                return;
            }

            foreach (var producerContext in this._producerNatsContexts)
            {
                await producerContext.Connection.ConnectAsync();

                if (producerContext.Connection.ConnectionState != NatsConnectionState.Open)
                {
                    this._logger.LogError("Unable to connect to NATS server {NatsServer}",
                        producerContext.Connection.Opts.Url);
                    return;
                }
            }

            this._logger.LogTrace("Connected to NATS server {NatsServer}", this._natsClientOptions.Url);

            try
            {
                var streamConfig = new StreamConfig(this._options.StreamName, [$"{this._providerName}.>"])
                {
                    Retention = StreamConfigRetention.Workqueue,
                    SubjectTransform = new SubjectTransform
                    {
                        Src = $"{this._providerName}.*.*",
                        Dest =
                            @$"{this._providerName}.{{{{partition({this._options.PartitionCount},1,2)}}}}.{{{{wildcard(1)}}}}.{{{{wildcard(2)}}}}"
                    }
                };

                await this._natsContext.CreateStreamAsync(streamConfig, cancellationToken);
            }
            catch (NatsJSApiException e) when (e.Error.ErrCode == 10065)
            {
                // ignore, stream already exists
            }

            this._logger.LogTrace(
                "Initialized to NATS JetStream stream {Stream} on server {NatsServer}",
                this._options.StreamName,
                this._natsClientOptions.Url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing NATS JetStream Connection Manager");
            throw;
        }
    }

    /// <summary>
    /// Enqueue a message to NATS JetStream stream
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task EnqueueMessage(NatsStreamMessage message, CancellationToken cancellationToken = default)
    {
        if (this._natsContext is null)
        {
            this._logger.LogError("Unable to enqueue message: NATS context is not initialized");
            throw new InvalidOperationException("Unable to enqueue message: NATS context is not initialized");
        }

        var ns = message.StreamId.Namespace.IsEmpty ? "null" : Encoding.UTF8.GetString(message.StreamId.Namespace.Span);
        var id = Encoding.UTF8.GetString(message.StreamId.Key.Span);

        var subject = $"{this._providerName}.{ns}.{id}";

        var context = this._producerNatsContexts[Math.Abs(id.GetHashCode()) % this._producerNatsContexts.Length];

        var ack = await context.TryPublishAsync(
            subject,
            message,
            this._natsClientOptions.SerializerRegistry.GetSerializer<NatsStreamMessage>(),
            cancellationToken: cancellationToken);

        if (ack.Success)
        {
            _logger.LogTrace("Enqueued NATS message to subject {Subject}", subject);
        }
        else
        {
            this._logger.LogError(ack.Error, "Failed to enqueue NATS message to {Subject}", subject);
        }
    }

    /// <summary>
    /// Create a NATS JetStream consumer
    /// </summary>
    /// <param name="partition">The partition number</param>
    /// <returns>A wrapper to a durable NATS JetStream stream consumer</returns>
    public NatsStreamConsumer CreateConsumer(uint partition) =>
        new(this._loggerFactory,
            this._natsContext,
            this._providerName,
            this._options.StreamName,
            partition,
            this._options.BatchSize,
            this._natsClientOptions.SerializerRegistry.GetDeserializer<NatsStreamMessage>());

    /// <summary>
    /// Acknowledge messages on a subject in a NATS JetStream stream
    /// </summary>
    /// <param name="subject">The ReplyTo subject</param>
    public async Task AcknowledgeMessages(string subject)
    {
        await this._natsConnection
            .PublishAsync(subject, AckPayload, serializer: NatsRawSerializer<byte[]>.Default);
    }
}
