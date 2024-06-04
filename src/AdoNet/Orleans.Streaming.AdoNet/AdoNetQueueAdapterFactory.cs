using System.Threading;
using Microsoft.Extensions.Hosting;

namespace Orleans.Streaming.AdoNet;

internal class AdoNetQueueAdapterFactory : IQueueAdapterFactory
{
    public AdoNetQueueAdapterFactory(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, HashRingStreamQueueMapperOptions hashOptions, ILoggerFactory loggerFactory, IHostApplicationLifetime lifetime, IServiceProvider serviceProvider)
    {
        _name = name;
        _streamOptions = streamOptions;
        _clusterOptions = clusterOptions;
        _cacheOptions = cacheOptions;
        _lifetime = lifetime;
        _serviceProvider = serviceProvider;

        _streamQueueMapper = new HashRingBasedStreamQueueMapper(hashOptions, name);
        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
        _adoNetQueueMapper = new AdoNetStreamQueueMapper(_streamQueueMapper);
    }

    private readonly string _name;
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly SimpleQueueCacheOptions _cacheOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServiceProvider _serviceProvider;

    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SimpleQueueAdapterCache _cache;
    private readonly AdoNetStreamQueueMapper _adoNetQueueMapper;

    private RelationalOrleansQueries _queries;

    /// <summary>
    /// Unfortunate implementation detail to account for lack of async lifetime.
    /// Ideally this concern will be moved upstream so this won't be needed.
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1);

    /// <summary>
    /// Ensures queries are loaded only once while allowing for recovery if the load fails.
    /// </summary>
    private ValueTask<RelationalOrleansQueries> GetQueriesAsync()
    {
        // attempt fast path
        return _queries is not null ? new(_queries) : new(CoreAsync());

        // slow path
        async Task<RelationalOrleansQueries> CoreAsync()
        {
            await _semaphore.WaitAsync(_streamOptions.InitializationTimeout, _lifetime.ApplicationStopping);
            try
            {
                // attempt fast path again
                if (_queries is not null)
                {
                    return _queries;
                }

                // slow path - the member variable will only be set if the call succeeds
                return _queries = await RelationalOrleansQueries
                    .CreateInstance(_streamOptions.Invariant, _streamOptions.ConnectionString)
                    .WaitAsync(_streamOptions.InitializationTimeout);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    public async Task<IQueueAdapter> CreateAdapter()
    {
        var queries = await GetQueriesAsync();

        return AdapterFactory(_serviceProvider, [_name, _streamOptions, _clusterOptions, _cacheOptions, _adoNetQueueMapper, queries]);
    }

    public async Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId)
    {
        var queries = await GetQueriesAsync();

        return HandlerFactory(_serviceProvider, [false, _streamOptions, _clusterOptions, _adoNetQueueMapper, queries]);
    }

    public IQueueAdapterCache GetQueueAdapterCache() => _cache;

    public IStreamQueueMapper GetStreamQueueMapper() => _streamQueueMapper;

    /// <summary>
    /// Used by the silo and client configurators as an entry point to set up a stream.
    /// </summary>
    public static IQueueAdapterFactory Create(IServiceProvider serviceProvider, string name)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(name);

        var streamOptions = serviceProvider.GetOptionsByName<AdoNetStreamOptions>(name);
        var clusterOptions = serviceProvider.GetProviderClusterOptions(name).Value;
        var cacheOptions = serviceProvider.GetOptionsByName<SimpleQueueCacheOptions>(name);
        var hashOptions = serviceProvider.GetOptionsByName<HashRingStreamQueueMapperOptions>(name);

        return QueueAdapterFactoryFactory(serviceProvider, [name, streamOptions, clusterOptions, cacheOptions, hashOptions]);
    }

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapterFactory"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapterFactory> QueueAdapterFactoryFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterFactory>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(HashRingStreamQueueMapperOptions)]);

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapter"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapter> AdapterFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapter>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(AdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);

    /// <summary>
    /// Factory of <see cref="AdoNetStreamFailureHandler"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetStreamFailureHandler> HandlerFactory = ActivatorUtilities.CreateFactory<AdoNetStreamFailureHandler>([typeof(bool), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(AdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);
}