using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Docs.Snippets.Streaming;

// <custom_queue_transport>
public interface ICustomQueueTransport
{
    Task SendAsync(
        QueueId queueId,
        StreamId streamId,
        object[] events,
        Dictionary<string, object>? requestContext,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CustomQueueMessage>> ReceiveAsync(
        QueueId queueId,
        int maxCount,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        QueueId queueId,
        IReadOnlyList<CustomQueueMessage> messages,
        CancellationToken cancellationToken);
}
// </custom_queue_transport>

// <custom_queue_adapter>
public sealed class CustomQueueAdapter(
    string name,
    ICustomQueueTransport transport,
    IStreamQueueMapper mapper) : IQueueAdapter
{
    public string Name => name;

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken? token,
        Dictionary<string, object>? requestContext)
    {
        if (token is not null)
        {
            throw new ArgumentException(
                "This adapter doesn't support caller-supplied sequence tokens.",
                nameof(token));
        }

        var queueId = mapper.GetQueueForStream(streamId);
        return transport.SendAsync(
            queueId,
            streamId,
            events.Cast<object>().ToArray(),
            requestContext,
            CancellationToken.None);
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new CustomQueueReceiver(queueId, transport);
}
// </custom_queue_adapter>

// <custom_queue_receiver>
public sealed class CustomQueueReceiver(
    QueueId queueId,
    ICustomQueueTransport transport) : IQueueAdapterReceiver
{
    public Task Initialize(TimeSpan timeout) => Task.CompletedTask;

    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount) =>
        GetQueueMessagesAsync(maxCount, CancellationToken.None);

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(
        int maxCount,
        CancellationToken cancellationToken)
    {
        var messages = await transport.ReceiveAsync(
            queueId,
            maxCount,
            cancellationToken);

        return messages.Cast<IBatchContainer>().ToList();
    }

    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task MessagesDeliveredAsync(IList<IBatchContainer> messages) =>
        MessagesDeliveredAsync(messages, CancellationToken.None);

    public Task MessagesDeliveredAsync(
        IList<IBatchContainer> messages,
        CancellationToken cancellationToken) =>
        transport.CompleteAsync(
            queueId,
            messages.Cast<CustomQueueMessage>().ToArray(),
            cancellationToken);

    public Task Shutdown(TimeSpan timeout) => Task.CompletedTask;
}
// </custom_queue_receiver>

// <custom_queue_batch>
public sealed class CustomQueueMessage(
    StreamId streamId,
    object[] events,
    long sequenceNumber,
    Dictionary<string, object>? requestContext) : IBatchContainer
{
    private readonly EventSequenceToken _sequenceToken = new(sequenceNumber);

    public StreamId StreamId { get; } = streamId;

    public StreamSequenceToken SequenceToken => _sequenceToken;

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() =>
        events.Cast<T>().Select(
            (item, index) => Tuple.Create<T, StreamSequenceToken>(
                item,
                _sequenceToken.CreateSequenceTokenForEvent(index)));

    public bool ImportRequestContext()
    {
        if (requestContext is null)
        {
            return false;
        }

        RequestContextExtensions.Import(requestContext);
        return true;
    }
}
// </custom_queue_batch>

// <custom_queue_factory>
public sealed class CustomQueueAdapterFactory : IQueueAdapterFactory
{
    private readonly string _name;
    private readonly ICustomQueueTransport _transport;
    private readonly IStreamQueueMapper _mapper;
    private readonly IQueueAdapterCache _cache;

    public CustomQueueAdapterFactory(
        string name,
        ICustomQueueTransport transport,
        HashRingStreamQueueMapperOptions mapperOptions,
        SimpleQueueCacheOptions cacheOptions,
        ILoggerFactory loggerFactory)
    {
        _name = name;
        _transport = transport;
        _mapper = new HashRingBasedStreamQueueMapper(mapperOptions, name);
        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
    }

    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task<IQueueAdapter> CreateAdapter() =>
        CreateAdapter(CancellationToken.None);

    public Task<IQueueAdapter> CreateAdapter(CancellationToken cancellationToken) =>
        Task.FromResult<IQueueAdapter>(
            new CustomQueueAdapter(_name, _transport, _mapper));

    public IQueueAdapterCache GetQueueAdapterCache() => _cache;

    public IStreamQueueMapper GetStreamQueueMapper() => _mapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId) =>
        Task.FromResult<IStreamFailureHandler>(
            new NoOpStreamDeliveryFailureHandler());

    public static IQueueAdapterFactory Create(IServiceProvider services, string name) =>
        new CustomQueueAdapterFactory(
            name,
            services.GetRequiredService<ICustomQueueTransport>(),
            services.GetOptionsByName<HashRingStreamQueueMapperOptions>(name),
            services.GetOptionsByName<SimpleQueueCacheOptions>(name),
            services.GetRequiredService<ILoggerFactory>());
}
// </custom_queue_factory>

public static class CustomQueueRegistration
{
    private const string ProviderName = "CustomQueue";

    // <custom_queue_silo_registration>
    public static void ConfigureSilo(
        ISiloBuilder builder,
        ICustomQueueTransport transport)
    {
        builder.Services.AddSingleton(transport);
        builder.AddPersistentStreams(
            ProviderName,
            CustomQueueAdapterFactory.Create,
            streams =>
            {
                streams.Configure<HashRingStreamQueueMapperOptions>(
                    options => options.Configure(
                        value => value.TotalQueueCount = 8));
                streams.Configure<SimpleQueueCacheOptions>(
                    options => options.Configure(
                        value => value.CacheSize = 4_096));
            });
    }
    // </custom_queue_silo_registration>

    // <custom_queue_client_registration>
    public static void ConfigureClient(
        IClientBuilder builder,
        ICustomQueueTransport transport)
    {
        builder.Services.AddSingleton(transport);
        builder.AddPersistentStreams(
            ProviderName,
            CustomQueueAdapterFactory.Create,
            streams => streams.Configure<HashRingStreamQueueMapperOptions>(
                options => options.Configure(
                    value => value.TotalQueueCount = 8)));
    }
    // </custom_queue_client_registration>
}
