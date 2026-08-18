using System.Text.Json;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streaming.EventHubs;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

[GenerateSerializer, Immutable]
public sealed class DeviceReading
{
    [Id(0)]
    public required string DeviceId { get; init; }

    [Id(1)]
    public double Value { get; init; }
}

internal sealed class ExternalEventEnvelope
{
    public int Version { get; init; }

    public required string StreamNamespace { get; init; }

    public required string StreamKey { get; init; }

    public required string EventType { get; init; }

    public required DeviceReading[] Events { get; init; }

    public string? CorrelationId { get; init; }
}

internal static class ExternalEventContract
{
    public const string CorrelationIdKey = "correlation-id";
    private const int CurrentVersion = 1;
    private const string DeviceReadingType = "device-reading";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(
        StreamId streamId,
        IEnumerable<T> events,
        Dictionary<string, object>? requestContext)
    {
        if (typeof(T) != typeof(DeviceReading))
        {
            throw new NotSupportedException(
                $"The external event contract does not define event type {typeof(T)}.");
        }

        string? correlationId = null;
        if (requestContext?.TryGetValue(CorrelationIdKey, out var value) == true)
        {
            correlationId = value as string
                ?? throw new InvalidDataException(
                    $"Request-context value '{CorrelationIdKey}' must be a string.");
        }

        var envelope = new ExternalEventEnvelope
        {
            Version = CurrentVersion,
            StreamNamespace = streamId.GetNamespace()
                ?? throw new InvalidOperationException(
                    "The external event contract requires a stream namespace."),
            StreamKey = streamId.GetKeyAsString(),
            EventType = DeviceReadingType,
            Events = events.Cast<DeviceReading>().ToArray(),
            CorrelationId = correlationId,
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static ExternalEventEnvelope Deserialize(string message)
    {
        var envelope = JsonSerializer.Deserialize<ExternalEventEnvelope>(message, JsonOptions)
            ?? throw new InvalidDataException("The queue message does not contain an event envelope.");

        if (envelope.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported event-envelope version {envelope.Version}.");
        }

        if (!string.Equals(envelope.EventType, DeviceReadingType, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported event type '{envelope.EventType}'.");
        }

        if (string.IsNullOrEmpty(envelope.StreamKey))
        {
            throw new InvalidDataException("The event envelope is missing its stream key.");
        }

        if (string.IsNullOrEmpty(envelope.StreamNamespace))
        {
            throw new InvalidDataException("The event envelope is missing its stream namespace.");
        }

        return envelope;
    }
}

// <azure_queue_data_adapter>
public sealed class JsonAzureQueueDataAdapter :
    IQueueDataAdapter<string, IBatchContainer>
{
    public string ToQueueMessage<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken? token,
        Dictionary<string, object>? requestContext)
    {
        if (token is not null)
        {
            throw new ArgumentException(
                "The Azure Queue stream provider assigns sequence positions when messages are read.",
                nameof(token));
        }

        return ExternalEventContract.Serialize(
            streamId,
            events,
            requestContext);
    }

    public IBatchContainer FromQueueMessage(
        string queueMessage,
        long sequenceId)
    {
        var envelope = ExternalEventContract.Deserialize(queueMessage);
        return new JsonAzureQueueBatch(
            StreamId.Create(envelope.StreamNamespace, envelope.StreamKey),
            envelope.Events,
            new EventSequenceTokenV2(sequenceId),
            envelope.CorrelationId);
    }
}
// </azure_queue_data_adapter>

// <azure_queue_batch_container>
[GenerateSerializer, Immutable]
public sealed class JsonAzureQueueBatch : IBatchContainer
{
    [Id(0)]
    private readonly DeviceReading[] _events;

    [Id(1)]
    private readonly EventSequenceTokenV2 _sequenceToken;

    [Id(2)]
    private readonly string? _correlationId;

    public JsonAzureQueueBatch(
        StreamId streamId,
        DeviceReading[] events,
        EventSequenceTokenV2 sequenceToken,
        string? correlationId)
    {
        StreamId = streamId;
        _events = events;
        _sequenceToken = sequenceToken;
        _correlationId = correlationId;
    }

    [Id(3)]
    public StreamId StreamId { get; }

    public StreamSequenceToken SequenceToken => _sequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        if (typeof(T) != typeof(DeviceReading))
        {
            return [];
        }

        return _events.Select(
            (item, index) => Tuple.Create(
                (T)(object)item,
                (StreamSequenceToken)_sequenceToken.CreateSequenceTokenForEvent(index)));
    }

    public bool ImportRequestContext()
    {
        if (_correlationId is null)
        {
            return false;
        }

        RequestContext.Set(
            ExternalEventContract.CorrelationIdKey,
            _correlationId);
        return true;
    }
}
// </azure_queue_batch_container>

public static class StreamDataAdapterRegistration
{
    private const string ProviderName = "ExternalEvents";

    // <azure_queue_data_adapter_registration>
    public static void ConfigureAzureQueueSilo(
        ISiloBuilder builder,
        QueueServiceClient queueServiceClient)
    {
        builder.AddAzureQueueStreams(
            ProviderName,
            (SiloAzureQueueStreamConfigurator streams) =>
            {
                streams.ConfigureAzureQueue(options => options.Configure(value =>
                {
                    value.QueueServiceClient = queueServiceClient;
                    value.QueueNames = ["external-events-0"];
                }));
                streams.ConfigureQueueDataAdapter<JsonAzureQueueDataAdapter>();
            });
    }

    public static void ConfigureAzureQueueClient(
        IClientBuilder builder,
        QueueServiceClient queueServiceClient)
    {
        builder.AddAzureQueueStreams(
            ProviderName,
            (ClusterClientAzureQueueStreamConfigurator streams) =>
            {
                streams.ConfigureAzureQueue(options => options.Configure(value =>
                {
                    value.QueueServiceClient = queueServiceClient;
                    value.QueueNames = ["external-events-0"];
                }));
                streams.ConfigureQueueDataAdapter<JsonAzureQueueDataAdapter>();
            });
    }
    // </azure_queue_data_adapter_registration>

    // <event_hub_data_adapter_registration>
    public static void ConfigureEventHubSilo(
        ISiloBuilder builder,
        string connectionString,
        string eventHubName,
        string consumerGroup,
        TableServiceClient checkpointStore)
    {
        builder.AddEventHubStreams(
            ProviderName,
            (ISiloEventHubStreamConfigurator streams) =>
            {
                streams.ConfigureEventHub(options => options.Configure(value =>
                    value.ConfigureEventHubConnection(
                        connectionString,
                        eventHubName,
                        consumerGroup)));
                streams.UseAzureTableCheckpointer(
                    options => options.Configure(
                        value => value.TableServiceClient = checkpointStore));
                streams.UseDataAdapter(
                    (services, _) =>
                        ActivatorUtilities.CreateInstance<CustomEventHubDataAdapter>(
                            services));
            });
    }

    public static void ConfigureEventHubClient(
        IClientBuilder builder,
        string connectionString,
        string eventHubName,
        string consumerGroup)
    {
        builder.AddEventHubStreams(
            ProviderName,
            (IClusterClientEventHubStreamConfigurator streams) =>
            {
                streams.ConfigureEventHub(options => options.Configure(value =>
                    value.ConfigureEventHubConnection(
                        connectionString,
                        eventHubName,
                        consumerGroup)));
                streams.UseDataAdapter(
                    (services, _) =>
                        ActivatorUtilities.CreateInstance<CustomEventHubDataAdapter>(
                            services));
            });
    }
    // </event_hub_data_adapter_registration>
}

internal sealed class CustomEventHubDataAdapter(Serializer serializer)
    : EventHubDataAdapter(serializer);
