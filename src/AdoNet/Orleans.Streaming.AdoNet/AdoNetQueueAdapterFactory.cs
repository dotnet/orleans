using System.Threading;
using Microsoft.Extensions.Hosting;

namespace Orleans.Streaming.AdoNet;

internal class AdoNetQueueAdapterFactory : IQueueAdapterFactory
{
    public AdoNetQueueAdapterFactory(string name, AdoNetStreamOptions streamOptions, ClusterOptions clusterOptions, SimpleQueueCacheOptions cacheOptions, HashRingStreamQueueMapperOptions hashOptions, StreamPullingAgentOptions agentOptions, ILoggerFactory loggerFactory, IServiceProvider serviceProvider, IHostApplicationLifetime lifetime)
    {
        _name = name;
        _streamOptions = streamOptions;
        _clusterOptions = clusterOptions;
        _cacheOptions = cacheOptions;
        _agentOptions = agentOptions;
        _serviceProvider = serviceProvider;
        _lifetime = lifetime;

        _streamQueueMapper = new HashRingBasedStreamQueueMapper(hashOptions, name);
        _cache = new SimpleQueueAdapterCache(cacheOptions, name, loggerFactory);
        _adoNetQueueMapper = new AdoNetStreamQueueMapper(_streamQueueMapper);
    }

    private readonly string _name;
    private readonly AdoNetStreamOptions _streamOptions;
    private readonly ClusterOptions _clusterOptions;
    private readonly SimpleQueueCacheOptions _cacheOptions;
    private readonly StreamPullingAgentOptions _agentOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly HashRingBasedStreamQueueMapper _streamQueueMapper;
    private readonly SimpleQueueAdapterCache _cache;
    private readonly AdoNetStreamQueueMapper _adoNetQueueMapper;

    private AdoNetQueueSweeper _sweeper;
    private RelationalOrleansQueries _queries;

    /// <summary>
    /// Unfortunate implementation detail to account for lack of async lifetime.
    /// Ideally this concern will be moved upstream so this won't be needed.
    /// </summary>
    private readonly SemaphoreSlim _semaphore = new(1);

    /// <summary>
    /// Ensures queries are loaded only once.
    /// </summary>
    /// <remarks>
    /// Concurrent calls will wait for the current attempt.
    /// Subsequent calls after failure will attempt again.
    /// Once the first attempt is successful that result is cached forever.
    /// Faults are propagated.
    /// </remarks>
    private ValueTask<RelationalOrleansQueries> GetQueriesAsync()
    {
        // attempt fast path
        return _queries is not null ? new(_queries) : new(CoreAsync());

        // slow path
        async Task<RelationalOrleansQueries> CoreAsync()
        {
            await _semaphore.WaitAsync(_lifetime.ApplicationStopping);
            try
            {
                // attempt fast path
                if (_queries is not null)
                {
                    return _queries;
                }

                // slow path - if this fails then the variable wont be set
                return _queries = await RelationalOrleansQueries.CreateInstance(_streamOptions.Invariant, _streamOptions.ConnectionString);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    /// <summary>
    /// Ensures the sweeper has started.
    /// </summary>
    /// <remarks>
    /// Concurrent calls will wait for the current attempt.
    /// Subsequent calls after failure will attempt again.
    /// Once the first attempt is successful that result is cached forever.
    /// Faults are propagated.
    /// </remarks>
    private Task EnsureSweeperStartedAsync(RelationalOrleansQueries queries)
    {
        // attempt fast path
        return _sweeper is not null ? _sweeper.Started : CoreAsync();

        // slow path
        async Task CoreAsync()
        {
            await _semaphore.WaitAsync(_lifetime.ApplicationStopping);
            try
            {
                // attempt fast path
                if (_sweeper is not null)
                {
                    await _sweeper.Started;
                    return;
                }

                // slow path
                var sweeper = SweeperFactory(_serviceProvider, [_name, _streamOptions, _clusterOptions, _adoNetQueueMapper, queries]);
                await sweeper.StartAsync(_lifetime.ApplicationStopping);

                sweeper.StopAsync(_lifetime.ApplicationStopping);

                // only keep the new sweeper if it started
                _sweeper = sweeper;
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

        return AdapterFactory(_serviceProvider, [_name, _streamOptions, _clusterOptions, _cacheOptions, _adoNetQueueMapper, _agentOptions, queries]);
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
        var agentOptions = serviceProvider.GetOptionsByName<StreamPullingAgentOptions>(name);

        return QueueAdapterFactoryFactory(serviceProvider, [name, streamOptions, clusterOptions, cacheOptions, hashOptions, agentOptions]);
    }

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapterFactory"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapterFactory> QueueAdapterFactoryFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapterFactory>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(HashRingStreamQueueMapperOptions), typeof(StreamPullingAgentOptions)]);

    /// <summary>
    /// Factory of <see cref="AdoNetQueueAdapter"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueAdapter> AdapterFactory = ActivatorUtilities.CreateFactory<AdoNetQueueAdapter>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(SimpleQueueCacheOptions), typeof(AdoNetStreamQueueMapper), typeof(StreamPullingAgentOptions), typeof(RelationalOrleansQueries)]);

    /// <summary>
    /// Factory of <see cref="AdoNetStreamFailureHandler"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetStreamFailureHandler> HandlerFactory = ActivatorUtilities.CreateFactory<AdoNetStreamFailureHandler>([typeof(bool), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(AdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);

    /// <summary>
    /// Factory of <see cref="AdoNetQueueSweeper"/> instances.
    /// </summary>
    private static readonly ObjectFactory<AdoNetQueueSweeper> SweeperFactory = ActivatorUtilities.CreateFactory<AdoNetQueueCleaner>([typeof(string), typeof(AdoNetStreamOptions), typeof(ClusterOptions), typeof(AdoNetStreamQueueMapper), typeof(RelationalOrleansQueries)]);
}