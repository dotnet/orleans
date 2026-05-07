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
internal sealed partial class NatsConnectionManager
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
                this.LogUnableToConnectToNatsServer(this._natsClientOptions.Url);
                return;
            }

            if (!this._natsConnection.ServerInfo!.JetStreamAvailable)
            {
                this.LogJetStreamUnavailable(this._natsClientOptions.Url, this._providerName);
                return;
            }

            foreach (var producerContext in this._producerNatsContexts)
            {
                await producerContext.Connection.ConnectAsync();

                if (producerContext.Connection.ConnectionState != NatsConnectionState.Open)
                {
                    this.LogUnableToConnectToNatsServer(producerContext.Connection.Opts.Url);
                    return;
                }
            }

            this.LogConnectedToNatsServer(this._natsClientOptions.Url);

            try
            {
                await this._natsContext.CreateStreamAsync(BuildStreamConfig(), cancellationToken);
            }
            catch (NatsJSApiException e) when (e.Error.ErrCode == 10065)
            {
                // ignore, stream already exists
            }
            catch (NatsJSApiException e) when (e.Error.ErrCode == 10058)
            {
                // Stream exists with different config — attempt in-place update
                // (safe for NumReplicas changes; NATS allows replica count upgrades)
                this.LogUpdatingExistingStream(this._options.StreamName);

                await this._natsContext.UpdateStreamAsync(BuildStreamConfig(), cancellationToken);
            }

            this.LogInitializedJetStreamStream(this._options.StreamName, this._natsClientOptions.Url);
        }
        catch (Exception ex)
        {
            this.LogErrorInitializingConnectionManager(ex);
            throw;
        }
    }

    private StreamConfig BuildStreamConfig() => new(this._options.StreamName, [$"{this._providerName}.>"])
    {
        Retention = StreamConfigRetention.Workqueue,
        NumReplicas = this._options.NumReplicas,
        Storage = this._options.StorageType,
        SubjectTransform = new SubjectTransform
        {
            Src = $"{this._providerName}.*.*",
            Dest =
                @$"{this._providerName}.{{{{partition({this._options.PartitionCount},1,2)}}}}.{{{{wildcard(1)}}}}.{{{{wildcard(2)}}}}"
        }
    };

    /// <summary>
    /// Enqueue a message to NATS JetStream stream
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task EnqueueMessage(NatsStreamMessage message, CancellationToken cancellationToken = default)
    {
        if (this._natsContext is null)
        {
            this.LogNatsContextNotInitialized();
            throw new InvalidOperationException("Unable to enqueue message because the NATS context is not initialized.");
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
            this.LogEnqueuedNatsMessage(subject);
        }
        else
        {
            this.LogFailedToEnqueueNatsMessage(ack.Error, subject);
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

    #region Logging

    [LoggerMessage(1, LogLevel.Error, "Unable to connect to NATS server '{NatsServer}'.")]
    private partial void LogUnableToConnectToNatsServer(string natsServer);

    [LoggerMessage(2, LogLevel.Error, "Unable to use NATS server '{NatsServer}' for Orleans stream provider '{ProviderName}': JetStream is not available.")]
    private partial void LogJetStreamUnavailable(string natsServer, string providerName);

    [LoggerMessage(3, LogLevel.Trace, "Connected to NATS server '{NatsServer}'.")]
    private partial void LogConnectedToNatsServer(string natsServer);

    [LoggerMessage(4, LogLevel.Information, "Stream '{Stream}' exists with a different configuration. Updating it.")]
    private partial void LogUpdatingExistingStream(string stream);

    [LoggerMessage(5, LogLevel.Trace, "Initialized JetStream stream '{Stream}' on NATS server '{NatsServer}'.")]
    private partial void LogInitializedJetStreamStream(string stream, string natsServer);

    [LoggerMessage(6, LogLevel.Error, "Error initializing the NATS JetStream connection manager.")]
    private partial void LogErrorInitializingConnectionManager(Exception exception);

    [LoggerMessage(7, LogLevel.Error, "Unable to enqueue message because the NATS context is not initialized.")]
    private partial void LogNatsContextNotInitialized();

    [LoggerMessage(8, LogLevel.Trace, "Enqueued NATS message to subject '{Subject}'.")]
    private partial void LogEnqueuedNatsMessage(string subject);

    [LoggerMessage(9, LogLevel.Error, "Failed to enqueue NATS message to subject '{Subject}'.")]
    private partial void LogFailedToEnqueueNatsMessage(Exception exception, string subject);

    #endregion Logging
}
