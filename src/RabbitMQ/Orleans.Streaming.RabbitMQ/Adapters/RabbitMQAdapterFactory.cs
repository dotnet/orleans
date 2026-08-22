using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.RabbitMQ.Adapters.Cache;
using Orleans.Streaming.RabbitMQ.RabbitMQ;
using Orleans.Streams;

namespace Orleans.Streaming.RabbitMQ.Adapters;

internal class RabbitMQAdapterFactory : IQueueAdapterFactory, ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    private readonly object _adapterLock = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _providerName;
    private readonly RabbitMQQueueCacheOptions _rabbitMqQueueCacheOptions;
    private readonly RabbitMQAdapterReceiverFactory _receiverFactory;
    private readonly Serializer _serializer;
    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly RabbitMQStreamSystemProvider _streamSystemProvider;
    private readonly RabbitMQQueueProvider _rabbitMqQueueProvider;
    private RabbitMQAdapter _adapter;
    private bool _disposed;

    public RabbitMQAdapterFactory(ILoggerFactory loggerFactory, string providerName,
        RabbitMQQueueCacheOptions rabbitMqQueueCacheOptions,
        RabbitMQClientOptions rabbitMqClientOptions, RabbitMQAdapterReceiverFactory receiverFactory,
        Serializer serializer, RabbitMQStreamSystemProvider streamSystemProvider, RabbitMQQueueProvider rabbitMqQueueProvider, HashRingStreamQueueMapperOptions hashRingStreamQueueMapperOptions)
    {
        _loggerFactory = loggerFactory;
        _providerName = providerName;
        _rabbitMqQueueCacheOptions = rabbitMqQueueCacheOptions;
        _receiverFactory = receiverFactory;
        _serializer = serializer;
        _streamQueueMapper = rabbitMqClientOptions.QueueNames is null or { Count: 0 }
            ? new HashRingBasedStreamQueueMapper(hashRingStreamQueueMapperOptions, providerName)
            : new HashRingBasedPartitionedStreamQueueMapper(rabbitMqClientOptions.QueueNames, providerName);
        _streamSystemProvider = streamSystemProvider;
        _rabbitMqQueueProvider = rabbitMqQueueProvider;
    }

    public Task<IQueueAdapter> CreateAdapter()
    {
        lock (_adapterLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult<IQueueAdapter>(
                _adapter ??= new RabbitMQAdapter(
                    _streamQueueMapper,
                    _rabbitMqQueueProvider,
                    _streamSystemProvider,
                    _loggerFactory,
                    _receiverFactory,
                    _serializer,
                    _providerName,
                    _rabbitMqQueueCacheOptions));
        }
    }

    public IQueueAdapterCache GetQueueAdapterCache() => new RabbitMqQueueCacheAdapter(_rabbitMqQueueCacheOptions);

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    public Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId _) =>
        Task.FromResult<IStreamFailureHandler>(new NoOpStreamDeliveryFailureHandler());

    public static RabbitMQAdapterFactory Create(IServiceProvider serviceProvider, string providerName)
    {
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var rabbitMqClientOptions = serviceProvider.GetOptionsByName<RabbitMQClientOptions>(providerName);
        var rabbitMqQueueCacheOptions = serviceProvider.GetOptionsByName<RabbitMQQueueCacheOptions>(providerName);
        var receiverFactory = serviceProvider.GetRequiredKeyedService<RabbitMQAdapterReceiverFactory>(providerName);
        var serializer = serviceProvider.GetService<Serializer>();
        var streamProvider = serviceProvider.GetRequiredKeyedService<RabbitMQStreamSystemProvider>(providerName);
        var rabbitMqQueueProvider = serviceProvider.GetRequiredKeyedService<RabbitMQQueueProvider>(providerName);
        var hashRingStreamQueueMapperOptions = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(providerName);

        return new RabbitMQAdapterFactory(loggerFactory, providerName,
            rabbitMqQueueCacheOptions, rabbitMqClientOptions, receiverFactory, serializer, streamProvider, rabbitMqQueueProvider, hashRingStreamQueueMapperOptions);
    }

    public void Participate(ISiloLifecycle lifecycle) =>
        lifecycle.Subscribe(
            $"{nameof(RabbitMQAdapterFactory)}-{_providerName}",
            ServiceLifecycleStage.ApplicationServices,
            _ => Task.CompletedTask,
            _ => DisposeAsync().AsTask());

    public async ValueTask DisposeAsync()
    {
        RabbitMQAdapter adapter;
        lock (_adapterLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            adapter = _adapter;
            _adapter = null;
        }

        if (adapter is not null)
        {
            await adapter.DisposeAsync().ConfigureAwait(false);
        }
    }
}